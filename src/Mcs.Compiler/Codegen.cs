using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mcs.Compiler;

public enum VK { Int, Bool, Fixed, Double, Array, String, Entity, Position, Block, Ref, Button, Text, Gui, Item, Void, Unsupported }

public sealed record Diag(string File, int Line, int Col, string Message)
{
    public override string ToString() => $"{File}({Line},{Col}): error MCS: {Message}";
}

sealed class UnsupportedException(SyntaxNode node, string message) : Exception(message)
{
    public SyntaxNode Node { get; } = node;
}

// ------------------------------------------------------------------ value model
abstract record Operand;
sealed record ConstOp(int Value) : Operand;
sealed record ScoreOp(string Holder) : Operand;

abstract class Var;
sealed class ScoreVar(string holder, VK kind) : Var { public string Holder = holder; public VK Kind = kind; }
sealed class StrVar(string? value) : Var { public string? Value = value; }
sealed class PosVar(Pos pos) : Var { public Pos Pos = pos; }
sealed class EntVar(string tag) : Var
{
    public string Tag = tag;
    public string? TypeId;              // minecraft:zombie; known only for entities created with new
    public bool Spawned;                // after a spawn, property writes are emitted directly
    public List<string> Deferred = new(); // pre-spawn property writes, applied after summon
}

/// Condition = a sequence of execute if/unless subcommands (AND), or a constant.
sealed record Cond(List<string> Parts, bool? Const)
{
    public static Cond Of(bool b) => new(new(), b);
    public static Cond Of(string part) => new(new() { part }, null);
}

sealed class FnInfo
{
    public required IMethodSymbol Sym;
    public required MethodDeclarationSyntax Decl;
    public required string Prefix;   // main, foo.bar
    public required string Path;     // ns:main, ns:foo/bar
    public Dictionary<IParameterSymbol, Var> Params = new(SymbolEqualityComparer.Default);
    public required string Obj;
    public string Ret => $"${Prefix}#ret {Obj}";
    public string Ctl => $"${Prefix}#ctl {Obj}";
}

sealed class LoopCtx { public bool HasBreak, HasContinue, IsEntity, IsWait; }

sealed class FnCtx(FnInfo info, SemanticModel model)
{
    public FnInfo Info = info;
    public SemanticModel Model = model;
    public List<string> Sink = new();
    public Dictionary<ISymbol, Var> Locals = new(SymbolEqualityComparer.Default);
    public HashSet<string> UsedHolders = new();
    public Stack<LoopCtx> Loops = new();
    public int Temp, Sub;
    public bool UsesCtl;
    public bool WaitCtl;       // uses Wait() and may return early: continuation calls check the ctl flag
    public int Depth;          // execution depth (each entity foreach / Exec.Run changes @s)
    public RefVar? This;       // instance methods of entity classes
    // objects (entity / block / item): created where, consumed / disposed where (compile-time single-use rule)
    public Dictionary<Var, (int depth, string? flag)> Objects = new(ReferenceEqualityComparer.Instance);
    public Dictionary<Var, SyntaxNode> Consumed = new(ReferenceEqualityComparer.Instance);
    public Dictionary<Var, SyntaxNode> Disposed = new(ReferenceEqualityComparer.Instance);
    public List<string> EntryResets = new();

    public (Dictionary<Var, SyntaxNode>, Dictionary<Var, SyntaxNode>) SaveObjects() => (new(Consumed, ReferenceEqualityComparer.Instance), new(Disposed, ReferenceEqualityComparer.Instance));
    public void RestoreObjects((Dictionary<Var, SyntaxNode> c, Dictionary<Var, SyntaxNode> d) s) { Consumed = new(s.c, ReferenceEqualityComparer.Instance); Disposed = new(s.d, ReferenceEqualityComparer.Instance); }
    /// After two branches: an object is "used" if either branch used it.
    public void MergeObjects((Dictionary<Var, SyntaxNode> c, Dictionary<Var, SyntaxNode> d) other)
    {
        foreach (var (k, v) in other.c) Consumed.TryAdd(k, v);
        foreach (var (k, v) in other.d) Disposed.TryAdd(k, v);
    }
}

// ------------------------------------------------------------------ code generator
public sealed partial class Codegen
{
    const int Scale = 1000;    // float: fixed-point ×1000 (1/1000)
    const int DScale = 65536;  // double: Q15.16 fixed-point (1/65536)

    static bool IsFrac(VK k) => k is VK.Fixed or VK.Double;
    static int ScaleOf(VK k) => k switch { VK.Fixed => Scale, VK.Double => DScale, _ => 1 };
    /// Scale factor for "execute store result ... <scale>" (score → NBT value).
    static string InvScale(VK k) => k switch { VK.Fixed => "0.001", VK.Double => "0.0000152587890625", _ => "1" };
    /// Formats a scaled constant as a plain number ("1.5").
    static string FmtScaled(int v, VK k) => k switch
    {
        VK.Fixed => (v / (double)Scale).ToString("0.###", CultureInfo.InvariantCulture),
        VK.Double => (v / (double)DScale).ToString("0.########", CultureInfo.InvariantCulture),
        _ => v.ToString(CultureInfo.InvariantCulture),
    };
    static long FloorDiv(long a, long b) => a / b - ((a % b != 0 && (a < 0) != (b < 0)) ? 1 : 0);

    readonly CSharpCompilation comp;
    readonly IReadOnlyList<SyntaxTree> userTrees;
    readonly string ns;
    readonly TargetProfile prof;
    readonly CompiledPack pack = new();
    public List<Diag> Diags { get; } = new();

    readonly Dictionary<IMethodSymbol, FnInfo> fns = new(SymbolEqualityComparer.Default);
    readonly Dictionary<IFieldSymbol, ScoreVar> statics = new(SymbolEqualityComparer.Default);
    readonly List<(ScoreVar v, int init)> staticInits = new();
    readonly SortedSet<int> consts = new();
    bool usesWorldSpawn;
    int storageCounter, macroCounter;
    FnCtx cur = null!;

    string Obj => $"mcs.{ns}";
    string Storage => $"{ns}:mcs";
    string WorldSpawnTag => $"mcs.{ns}.spawn";

    public Codegen(CSharpCompilation comp, IReadOnlyList<SyntaxTree> userTrees, string ns, TargetProfile prof)
    {
        this.comp = comp; this.userTrees = userTrees; this.ns = ns; this.prof = prof;
    }

    public CompiledPack Run()
    {
        // 0. m.Mount(...) bindings (compile-time, so they are known before any field is declared)
        CollectMounts();

        // 1. Collect declarations
        foreach (var tree in userTrees)
        {
            var model = comp.GetSemanticModel(tree);
            var root = tree.GetRoot();
            foreach (var fd in root.DescendantNodes().OfType<FieldDeclarationSyntax>())
                foreach (var v in fd.Declaration.Variables)
                    DeclareStatic(model, v);
            foreach (var md in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                var sym = model.GetDeclaredSymbol(md)!;
                if (HasAttr(sym, "InlineAttribute")) continue;   // expanded at each call site
                if (!sym.IsStatic && !IsEntityClass(sym.ContainingType)) { Error(md.Identifier, "Instance methods are only supported in entity classes ([Entity])."); continue; }
                string prefix = MethodPrefix(sym);
                while (fns.Values.Any(f => f.Prefix == prefix)) prefix += "_";
                var info = new FnInfo { Sym = sym, Decl = md, Prefix = prefix, Path = $"{ns}:{prefix.Replace('.', '/')}", Obj = Obj };
                bool badParam = false;
                foreach (var p in sym.Parameters)
                {
                    try {
                    info.Params[p] = Kind(p.Type) switch
                    {
                        VK.Int or VK.Bool or VK.Fixed or VK.Double => new ScoreVar(H($"${prefix}.{p.Name}"), Kind(p.Type)),
                        VK.Entity => new EntVar($"mcs.{ns}.{prefix}.{p.Name}") { Spawned = true },
                        VK.Array => Unsup<Var>(md, $"Array parameters ({p.Name}) are only supported in [Inline] methods — or use a static array field."),
                        VK.Ref => new RefVar($"{(ClassOf(p.Type)?.IsPlayer == true ? "@a" : "@e")}[tag=mcs.{ns}.{prefix}.{p.Name}]") { Tag = $"mcs.{ns}.{prefix}.{p.Name}", Type = p.Type },
                        _ => Unsup<Var>(md, $"Unsupported parameter type: {p.Type}"),
                    };
                    } catch (UnsupportedException e) { Error(p.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() ?? e.Node, e.Message); badParam = true; }
                }
                if (!badParam) fns[sym] = info;
            }
        }

        CollectClickObjects();   // before any item stack is generated

        // 2. Generate functions
        foreach (var info in fns.Values)
        {
            var model = comp.GetSemanticModel(info.Decl.SyntaxTree);
            GenFunction(info, model);
        }

        // 3. Load function and tags
        var load = new List<string> { $"scoreboard objectives add {Obj} dummy" };
        // top-level asm runs first, in source order (e.g. creating objectives that m.Mount uses)
        foreach (var f in fns.Values.Where(f => f.Sym.Name.StartsWith(Preprocessor.TopAsmPrefix))) load.Add($"function {f.Path}");
        FinishLibs(load);
        FinishMath();
        FinishGui(load);
        FinishObjects(load);
        CollectEvents();
        FinishEvents(load);
        foreach (var obj in mountObjectives) load.Add($"scoreboard objectives add {obj} dummy");
        FinishEntities(load);
        foreach (var c in consts) load.Add($"scoreboard players set {H($"#{c}")} {c}");
        foreach (var (v, init) in staticInits)
            load.Add($"execute unless score {v.Holder} = {v.Holder} run scoreboard players set {v.Holder} {init}");
        // Things that need the world (spawn marker, [Load], Main)
        var start = new List<string>();
        if (usesWorldSpawn)
        {
            // Functions run from #load/schedule use world spawn as their execution position
            start.Add($"kill @e[type=minecraft:marker,tag={WorldSpawnTag}]");
            start.Add($"summon minecraft:marker ~ ~ ~ {{Tags:[\"{WorldSpawnTag}\"]}}");
        }
        foreach (var f in fns.Values.Where(f => HasAttr(f.Sym, "LoadAttribute"))) start.Add($"function {f.Path}");
        var main = fns.Values.FirstOrDefault(f => f.Sym.Name == "Main" && f.Sym.ContainingType.Name == Preprocessor.GlobalClass && f.Sym.Parameters.Length == 0);
        if (main != null) start.Add($"function {main.Path}");

        // Main/[Load] run at world spawn. Commands that change the world there fail when its chunk is not loaded.
        bool touchesWorld = usesWorldSpawn || pack.Functions.Values.Any(f => f.Any(l => WorldCommand.IsMatch(l)));
        if (touchesWorld && prof.ForceloadSpawn)
        {
            // 1.21.9+: spawn chunks no longer exist, so nothing is loaded when #load runs (e.g. a server with no
            // players online). Force-load the spawn chunk and start once it has loaded, like older versions did.
            load.Add("forceload add ~ ~");
            load.Add($"function {ns}:__boot");
            pack.Functions[$"{ns}:__boot"] = new()
            {
                $"execute if loaded ~ ~ ~ run function {ns}:__start",
                $"execute unless loaded ~ ~ ~ run schedule function {ns}:__boot 1t",
            };
            pack.Functions[$"{ns}:__start"] = start;
        }
        else load.AddRange(start);
        pack.Functions[$"{ns}:__load"] = load;
        pack.LoadTag.Add($"{ns}:__load");
        foreach (var f in fns.Values.Where(f => HasAttr(f.Sym, "TickAttribute"))) pack.TickTag.Add(f.Path);
        return pack;
    }

    static readonly System.Text.RegularExpressions.Regex WorldCommand =
        new(@"(^|\brun )(summon|setblock|fill|clone|place|particle|playsound) ", System.Text.RegularExpressions.RegexOptions.Compiled);

    void DeclareStatic(SemanticModel model, VariableDeclaratorSyntax v)
    {
        if (model.GetDeclaredSymbol(v) is not IFieldSymbol f) return;
        if (f.IsConst) return; // constants are folded by Roslyn
        if (f.Name.StartsWith(Preprocessor.MountFieldPrefix)) return;   // a top-level m.Mount(...)
        if (f.IsStatic && (mounts.ContainsKey(f) || IsDummyClass(f.ContainingType))) { DeclareMounted(model, f, v); return; }
        if (!f.IsStatic && IsEntityClass(f.ContainingType)) { ObjectiveOf(f); return; }   // per-entity score
        if (!f.IsStatic) { Error(v, "Only static fields (or fields of entity classes) are supported."); return; }
        if (f.IsReadOnly && f.Type is IArrayTypeSymbol) { DeclareArray(model, f, v); return; }
        var k = Kind(f.Type);
        if (k == VK.Gui) { DeclareGuiStatic(model, f, v); return; }
        if (k == VK.Item) { DeclareItemStatic(model, f, v); return; }
        if (k == VK.Array) { DeclareArrayField(model, f, v); return; }
        if (k is not (VK.Int or VK.Bool or VK.Fixed or VK.Double)) { Error(v, $"Unsupported static field type: {f.Type} (only int/bool/float/double are supported)"); return; }
        string cls = f.ContainingType.Name == Preprocessor.GlobalClass ? "" : Snake(f.ContainingType.Name) + ".";
        var sv = new ScoreVar(H($"$static.{cls}{f.Name}"), k);
        statics[f] = sv;
        int init = 0;
        if (v.Initializer != null)
        {
            var cv = model.GetConstantValue(v.Initializer.Value);
            if (!cv.HasValue) { Error(v.Initializer, "Static field initializers must be compile-time constants."); }
            else init = ScaleConst(cv.Value, k);
        }
        staticInits.Add((sv, init));
    }

    // ================================================================ function
    void GenFunction(FnInfo info, SemanticModel model)
    {
        cur = new FnCtx(info, model);
        foreach (var (p, v) in info.Params) { cur.Locals[p] = v; if (v is ScoreVar sv) cur.UsedHolders.Add(sv.Holder); }
        var d = info.Decl;
        bool tagThis = false;
        if (!info.Sym.IsStatic)
        {
            tagThis = d.DescendantNodes().Any(n => n is ForEachStatementSyntax or LambdaExpressionSyntax);
            cur.This = new RefVar("@s") { Depth = 0, Tag = tagThis ? $"mcs.{ns}.this.{info.Prefix}" : null, Type = info.Sym.ContainingType };
            if (tagThis) Emit($"tag @s add {cur.This.Tag}");
        }
        try
        {
            if (d.Body != null && ContainsWait(d.Body) && MayAbort(d.Body)) cur.WaitCtl = cur.UsesCtl = true;
            if (d.Body != null) EmitStatements(d.Body.Statements);
            else if (d.ExpressionBody != null)
            {
                if (Kind(info.Sym.ReturnType) == VK.Void) EmitExprStatement(d.ExpressionBody.Expression);
                else AssignHolder(new ScoreVar(info.Ret, Kind(info.Sym.ReturnType)), d.ExpressionBody.Expression);
            }
        }
        catch (UnsupportedException e) { Error(e.Node, e.Message); }
        if (tagThis) Emit($"tag @s remove {cur.This!.Tag}");
        cur.Sink.InsertRange(0, cur.EntryResets);
        if (cur.UsesCtl) cur.Sink.Insert(0, $"scoreboard players set {info.Ctl} 0");
        pack.Functions[info.Path] = cur.Sink;
    }

    /// k: continuation function to call after the last statement (set inside code that contains Wait()).
    void EmitStatements(IEnumerable<StatementSyntax> stmts, string? k = null)
    {
        var list = stmts.ToList();
        for (int i = 0; i < list.Count; i++)
        {
            if (IsWaitStmt(list[i]))
            {
                // Everything after this statement becomes a function: the continuation
                var rest = list.Skip(i + 1).ToList();
                try
                {
                    CheckWaitContext(list[i]);
                    string? kr = rest.Count > 0 ? ContFn("wait", () => EmitStatements(rest, k)) : k;
                    EmitWaitStmt(list[i], kr);
                }
                catch (UnsupportedException e) { Error(e.Node, e.Message); }
                return;
            }
            try { EmitStmt(list[i]); }
            catch (UnsupportedException e) { Error(e.Node, e.Message); }
            if (i < list.Count - 1 && cur.UsesCtl && MayAbort(list[i]))
            {
                // Run the rest only if no return/break/continue happened
                var rest = list.Skip(i + 1).ToList();
                var cmds = Capture(() => EmitStatements(rest, k));
                EmitGuarded($"if score {cur.Info.Ctl} matches 0", cmds, "cont");
                return;
            }
        }
        if (k != null) EmitCont(k);
    }

    // ================================================================ Wait() — continuation splitting
    // Wait() schedules "the rest of the method" one tick later and returns. Statements that contain a Wait()
    // are compiled in continuation-passing style: every branch / loop body ends by calling its continuation,
    // and Wait() schedules the continuation instead of calling it.

    bool IsWaitCall(ExpressionSyntax e) => e is InvocationExpressionSyntax inv && Intrinsic(cur.Model.GetSymbolInfo(inv).Symbol) == "wait";

    bool ContainsWait(SyntaxNode n) => n.DescendantNodesAndSelf(x => x is not (LambdaExpressionSyntax or LocalFunctionStatementSyntax or AnonymousMethodExpressionSyntax))
        .OfType<InvocationExpressionSyntax>().Any(IsWaitCall);

    bool IsWaitStmt(StatementSyntax s) => s switch
    {
        ExpressionStatementSyntax es => IsWaitCall(es.Expression),
        BlockSyntax or IfStatementSyntax or WhileStatementSyntax or ForStatementSyntax or DoStatementSyntax => ContainsWait(s),
        _ => false,   // other statements reach the "wait" intrinsic, which reports an error
    };

    void CheckWaitContext(SyntaxNode at)
    {
        if (cur.Depth > 0 || cur.This != null)
            throw new UnsupportedException(at, "Wait() cannot be used in entity contexts (instance methods, foreach over entities, Run): the execution context (@s, position) is lost after a tick.");
        if (!cur.Info.Sym.ReturnsVoid)
            throw new UnsupportedException(at, "Methods that use Wait() must return void (the caller continues before the method finishes).");
    }

    static List<StatementSyntax> Stmts(StatementSyntax s) => s is BlockSyntax b ? b.Statements.ToList() : new() { s };

    /// A continuation function (also a possible schedule target, so it resets the control flag).
    string ContFn(string kind, Action body)
    {
        string path = ReservePath(kind);
        var cmds = Capture(body);
        if (cur.WaitCtl) cmds.Insert(0, $"scoreboard players set {cur.Info.Ctl} 0");
        pack.Functions[path] = cmds;
        return path;
    }

    void EmitCont(string k) => Emit(cur.WaitCtl ? $"execute if score {cur.Info.Ctl} matches 0 run function {k}" : $"function {k}");

    void EmitWaitStmt(StatementSyntax s, string? kr)
    {
        switch (s)
        {
            case ExpressionStatementSyntax es:
            {
                var args = ((InvocationExpressionSyntax)es.Expression).ArgumentList.Arguments;
                int ticks = args.Count > 0 ? ConstSlot(args[0].Expression) : 1;
                if (ticks < 1) throw new UnsupportedException(es, "Wait(ticks) needs at least 1 tick.");
                if (kr != null) Emit($"schedule function {kr} {ticks}t append");
                break;
            }
            case BlockSyntax b: EmitStatements(b.Statements, kr); break;
            case IfStatementSyntax ifs: EmitWaitIf(ifs, kr); break;
            case WhileStatementSyntax ws: EmitWaitLoop(ws.Condition, ws.Statement, null, kr); break;
            case ForStatementSyntax fs:
                if (fs.Declaration != null)
                    foreach (var v in fs.Declaration.Variables)
                        DeclareLocal((ILocalSymbol)cur.Model.GetDeclaredSymbol(v)!, v.Initializer?.Value, v);
                foreach (var init in fs.Initializers) EmitExprStatement(init);
                EmitWaitLoop(fs.Condition, fs.Statement, fs.Incrementors, kr);
                break;
            case DoStatementSyntax ds: EmitWaitDo(ds, kr); break;
        }
    }

    // The branch temp t is set to 2 at the start of each branch: the continuation may loop back through this
    // same code and overwrite t, and without the reset the second dispatch line could also fire on return.
    void EmitWaitIf(IfStatementSyntax s, string? kr)
    {
        var c = EvalCond(s.Condition);
        if (c.Const is bool b)
        {
            if (b) EmitStatements(Stmts(s.Statement), kr);
            else if (s.Else != null) EmitStatements(Stmts(s.Else.Statement), kr);
            else if (kr != null) EmitCont(kr);
            return;
        }
        string t = Materialize(c);
        var before = cur.SaveObjects();
        var thenCmds = Capture(() => { Emit($"scoreboard players set {t} 2"); EmitStatements(Stmts(s.Statement), kr); });
        var afterThen = cur.SaveObjects();
        cur.RestoreObjects(before);
        var elseCmds = Capture(() =>
        {
            Emit($"scoreboard players set {t} 2");
            if (s.Else != null) EmitStatements(Stmts(s.Else.Statement), kr);
            else if (kr != null) EmitCont(kr);
        });
        cur.MergeObjects(afterThen);
        Emit($"execute if score {t} matches 1 run function {NewSub("if", thenCmds)}");
        if (elseCmds.Count > 1) Emit($"execute if score {t} matches 0 run function {NewSub("else", elseCmds)}");
    }

    /// head: condition → body or exit(kr); body ends by calling next (incrementors + head).
    void EmitWaitLoop(ExpressionSyntax? cond, StatementSyntax stmt, SeparatedSyntaxList<ExpressionSyntax>? incrementors, string? kr)
    {
        string head = ReservePath("while"), body = ReservePath("loop");
        string? t = null;
        var headCmds = Capture(() => t = EmitLoopTest(cond, body, kr));
        if (cur.WaitCtl) headCmds.Insert(0, $"scoreboard players set {cur.Info.Ctl} 0");
        pack.Functions[head] = headCmds;
        string next = incrementors is { Count: > 0 } incs
            ? ContFn("next", () => { foreach (var inc in incs) EmitExprStatement(inc); Emit($"function {head}"); })
            : head;
        EmitWaitBody(body, t, stmt, next);
        Emit($"function {head}");
    }

    void EmitWaitDo(DoStatementSyntax ds, string? kr)
    {
        string body = ReservePath("do");
        string? t = null;
        string check = ContFn("dowhile", () => t = EmitLoopTest(ds.Condition, body, kr));
        EmitWaitBody(body, t, ds.Statement, check);
        Emit($"function {body}");
    }

    /// Emits "if cond → function body, else → kr". Returns the branch temp (null for a constant condition).
    string? EmitLoopTest(ExpressionSyntax? cond, string body, string? kr)
    {
        var c = cond == null ? Cond.Of(true) : EvalCond(cond);
        if (c.Const == true) { Emit($"function {body}"); return null; }
        if (c.Const == false) { if (kr != null) EmitCont(kr); return null; }
        string t = Materialize(c);
        Emit($"execute if score {t} matches 1 run function {body}");
        if (kr != null) Emit($"execute if score {t} matches 0 run function {NewSub("exit", new() { $"scoreboard players set {t} 2", $"function {kr}" })}");
        return t;
    }

    void EmitWaitBody(string path, string? t, StatementSyntax stmt, string next)
    {
        cur.Loops.Push(new LoopCtx { IsWait = true });
        var cmds = Capture(() =>
        {
            if (t != null) Emit($"scoreboard players set {t} 2");
            EmitStatements(Stmts(stmt), next);
        });
        cur.Loops.Pop();
        if (cur.WaitCtl) cmds.Insert(0, $"scoreboard players set {cur.Info.Ctl} 0");
        pack.Functions[path] = cmds;
    }

    static bool MayAbort(SyntaxNode s) => s.DescendantNodesAndSelf(n => n is not (LambdaExpressionSyntax or LocalFunctionStatementSyntax))
        .Any(n => n is ReturnStatementSyntax or BreakStatementSyntax or ContinueStatementSyntax);

    void EmitStmt(StatementSyntax s)
    {
        switch (s)
        {
            case BlockSyntax b: EmitStatements(b.Statements); break;
            case EmptyStatementSyntax: break;
            case LocalDeclarationStatementSyntax ld:
                if (ld.IsConst) break;
                foreach (var v in ld.Declaration.Variables)
                    DeclareLocal((ILocalSymbol)cur.Model.GetDeclaredSymbol(v)!, v.Initializer?.Value, v);
                break;
            case ExpressionStatementSyntax es: EmitExprStatement(es.Expression); break;
            case IfStatementSyntax ifs: EmitIf(ifs); break;
            case WhileStatementSyntax ws: EmitWhile(ws.Condition, ws.Statement, null); break;
            case ForStatementSyntax fs:
                if (fs.Declaration != null)
                    foreach (var v in fs.Declaration.Variables)
                        DeclareLocal((ILocalSymbol)cur.Model.GetDeclaredSymbol(v)!, v.Initializer?.Value, v);
                foreach (var init in fs.Initializers) EmitExprStatement(init);
                EmitWhile(fs.Condition, fs.Statement, fs.Incrementors);
                break;
            case DoStatementSyntax ds: EmitDoWhile(ds); break;
            case ForEachStatementSyntax fe: EmitForEach(fe); break;
            case SwitchStatementSyntax sw: EmitSwitch(sw); break;
            case ReturnStatementSyntax rs: EmitReturn(rs); break;
            case BreakStatementSyntax bs:
                if (cur.Loops.Count == 0) throw new UnsupportedException(bs, "break is only supported inside loops (switch is not supported).");
                if (cur.Loops.Peek().IsWait) throw new UnsupportedException(bs, "break cannot be used in a loop that contains Wait().");
                cur.Loops.Peek().HasBreak = true; cur.UsesCtl = true;
                Emit($"scoreboard players set {cur.Info.Ctl} 2");
                break;
            case ContinueStatementSyntax cs:
                if (cur.Loops.Count > 0 && cur.Loops.Peek().IsWait) throw new UnsupportedException(cs, "continue cannot be used in a loop that contains Wait().");
                cur.Loops.Peek().HasContinue = true; cur.UsesCtl = true;
                Emit($"scoreboard players set {cur.Info.Ctl} 3");
                break;
            default:
                throw new UnsupportedException(s, $"Unsupported statement: {s.Kind()}");
        }
    }

    void DeclareLocal(ILocalSymbol sym, ExpressionSyntax? init, SyntaxNode at)
    {
        var k = Kind(sym.Type);
        switch (k)
        {
            case VK.Int or VK.Bool or VK.Fixed or VK.Double:
                var sv = new ScoreVar(UniqueHolder(sym.Name), k);
                cur.Locals[sym] = sv;
                if (init != null) AssignHolder(sv, init);
                break;
            case VK.Array:
                if (init == null) throw new UnsupportedException(at, $"{sym.Name} needs a value (new {sym.Type.ToDisplayString().TrimEnd('[', ']')}[n] or {{ … }}).");
                cur.Locals[sym] = ArrayValue(init, sym.Name, ElemKind(sym.Type));
                break;
            case VK.String:
                cur.Locals[sym] = new StrVar(init == null ? null : ConstString(init));
                break;
            case VK.Entity:
                if (init == null) { cur.Locals[sym] = new EntVar(EntityTag(sym.Name)); break; }
                if (init is BaseObjectCreationExpressionSyntax oc && Intrinsic(cur.Model.GetSymbolInfo(oc).Symbol) is ("entity.new" or "display.new") and var ctor)
                {
                    var nev = new EntVar(EntityTag(sym.Name));
                    if (ctor == "entity.new")
                    {
                        var arg = oc.ArgumentList!.Arguments[0].Expression;
                        if (cur.Model.GetSymbolInfo(arg).Symbol is not IFieldSymbol ef)
                            throw new UnsupportedException(arg, "The entity type must be a constant (e.g. EntityType.Zombie).");
                        nev.TypeId = ResolveId("entities", McIdOf(ef) ?? Snake(ef.Name), arg);
                    }
                    cur.Locals[sym] = nev;
                    RegisterObject(nev, sym, at);
                    InitEntityObject(nev, oc);
                }
                else if (IsCloneCall(init, out var cloneSrc))
                {
                    cur.Locals[sym] = CloneEntity(GetEnt(cloneSrc), EntityTag(sym.Name));
                    RegisterObject(cur.Locals[sym], sym, at);
                }
                else cur.Locals[sym] = GetEnt(init); // alias
                break;
            case VK.Position:
                cur.Locals[sym] = new PosVar(init == null ? Pos.Here : EvalPos(init));
                break;
            case VK.Block or VK.Ref or VK.Button or VK.Text or VK.Gui or VK.Item:
                if (init == null) throw new UnsupportedException(at, $"{sym.Name} needs an initial value.");
                cur.Locals[sym] = EvalLibObject(init, k, sym);
                if (k is VK.Block or VK.Item && (init is BaseObjectCreationExpressionSyntax || IsCloneCall(init, out _))) RegisterObject(cur.Locals[sym], sym, at);
                break;
            default:
                throw new UnsupportedException(at, sym.Type is IArrayTypeSymbol
                    ? $"Unsupported array type: {sym.Type}. Arrays hold int, bool, float or double (string arrays only as static readonly tables)."
                    : $"Unsupported variable type: {sym.Type}");
        }
    }

    string EntityTag(string name)
    {
        string baseTag = $"mcs.{ns}.{cur.Info.Prefix}.{name}", t = baseTag;
        for (int n = 2; !cur.UsedHolders.Add("tag:" + t); n++) t = $"{baseTag}_{n}";
        return t;
    }

    string UniqueHolder(string name)
    {
        string b = $"${cur.Info.Prefix}.{name}", h = b;
        for (int n = 2; !cur.UsedHolders.Add(H(h)); n++) h = $"{b}_{n}";
        return H(h);
    }

    string Temp() => H($"${cur.Info.Prefix}#t{cur.Temp++}");
    /// A score holder in this pack's objective: "name objective"
    string H(string name) => $"{name} {Obj}";

    void EmitReturn(ReturnStatementSyntax rs)
    {
        if (rs.Expression != null && Kind(cur.Info.Sym.ReturnType) == VK.Ref)
        {
            string rt = RetTag(cur.Info);
            Emit($"tag @e[tag={rt}] remove {rt}");
            if (!rs.Expression.IsKind(SyntaxKind.NullLiteralExpression)) Emit($"tag {GetRef(rs.Expression).Selector} add {rt}");
        }
        else if (rs.Expression != null)
        {
            var k = Kind(cur.Info.Sym.ReturnType);
            if (k is not (VK.Int or VK.Bool or VK.Fixed or VK.Double)) throw new UnsupportedException(rs, $"Unsupported return type: {cur.Info.Sym.ReturnType}");
            AssignHolder(new ScoreVar(cur.Info.Ret, k), rs.Expression);
        }
        bool isTail = rs.Parent == cur.Info.Decl.Body && cur.Info.Decl.Body!.Statements.Last() == rs;
        if (!isTail)
        {
            cur.UsesCtl = true;
            Emit($"scoreboard players set {cur.Info.Ctl} 1");
        }
    }

    // ================================================================ control flow
    void EmitIf(IfStatementSyntax s)
    {
        var c = EvalCond(s.Condition);
        if (c.Const == false && s.Else == null) return;          // compile-time false: body is not generated
        if (c.Const == true) { EmitStmt(s.Statement); return; }
        if (s.Else == null)
        {
            var body = Capture(() => EmitStmt(s.Statement));
            EmitGuarded(c, body, "if");
            return;
        }
        if (c.Const is bool b) { EmitStmt(b ? s.Statement : s.Else.Statement); return; }
        string t = Materialize(c);
        var before = cur.SaveObjects();
        var thenCmds = Capture(() => EmitStmt(s.Statement));
        var afterThen = cur.SaveObjects();
        cur.RestoreObjects(before);
        var elseCmds = Capture(() => EmitStmt(s.Else.Statement));
        cur.MergeObjects(afterThen);
        EmitGuarded($"if score {t} matches 1", thenCmds, "if");
        EmitGuarded($"if score {t} matches 0", elseCmds, "else");
    }

    void EmitWhile(ExpressionSyntax? cond, StatementSyntax stmt, SeparatedSyntaxList<ExpressionSyntax>? incrementors) =>
        EmitLoop(() => cond == null ? Cond.Of(true) : EvalCond(cond), () => EmitStmt(stmt), MayAbort(stmt),
            incrementors == null ? null : () => { foreach (var inc in incrementors) EmitExprStatement(inc); });

    void EmitLoop(Func<Cond> cond, Action emitBody, bool abortable, Action? incr)
    {
        var loop = new LoopCtx();
        string head = ReservePath("while"), body = ReservePath("loop");
        var headCmds = Capture(() => EmitGuarded(cond(), new() { $"function {body}" }, "x"));
        cur.Loops.Push(loop);
        var bodyCmds = Capture(() =>
        {
            emitBody();
            if (loop.HasContinue) Emit($"execute if score {cur.Info.Ctl} matches 3 run scoreboard players set {cur.Info.Ctl} 0");
            var tail = Capture(() =>
            {
                incr?.Invoke();
                Emit($"function {head}");
            });
            if (abortable) EmitGuarded($"if score {cur.Info.Ctl} matches 0", tail, "next");
            else cur.Sink.AddRange(tail);
        });
        cur.Loops.Pop();
        pack.Functions[head] = headCmds;
        pack.Functions[body] = bodyCmds;
        Emit($"function {head}");
        if (loop.HasBreak) Emit($"execute if score {cur.Info.Ctl} matches 2 run scoreboard players set {cur.Info.Ctl} 0");
    }

    void EmitDoWhile(DoStatementSyntax ds)
    {
        var loop = new LoopCtx();
        string body = ReservePath("do");
        cur.Loops.Push(loop);
        var bodyCmds = Capture(() =>
        {
            EmitStmt(ds.Statement);
            if (loop.HasContinue) Emit($"execute if score {cur.Info.Ctl} matches 3 run scoreboard players set {cur.Info.Ctl} 0");
            var tail = Capture(() => EmitGuarded(EvalCond(ds.Condition), new() { $"function {body}" }, "x"));
            if (MayAbort(ds.Statement)) EmitGuarded($"if score {cur.Info.Ctl} matches 0", tail, "next");
            else cur.Sink.AddRange(tail);
        });
        cur.Loops.Pop();
        pack.Functions[body] = bodyCmds;
        Emit($"function {body}");
        if (loop.HasBreak) Emit($"execute if score {cur.Info.Ctl} matches 2 run scoreboard players set {cur.Info.Ctl} 0");
    }

    void EmitGuarded(Cond c, List<string> cmds, string kind)
    {
        if (c.Const == false || cmds.Count == 0) return;
        if (c.Parts.Count == 0) { cur.Sink.AddRange(cmds); return; }
        EmitGuarded(string.Join(" ", c.Parts), cmds, kind);
    }

    /// Runs cmds under an execute condition: inline for a single command, otherwise via a sub-function.
    void EmitGuarded(string guard, List<string> cmds, string kind)
    {
        if (cmds.Count == 0) return;
        string inner = cmds.Count == 1 ? cmds[0] : $"function {NewSub(kind, cmds)}";
        Emit(inner.StartsWith("execute ") ? $"execute {guard} {inner[8..]}" : $"execute {guard} run {inner}");
    }

    string ReservePath(string kind) => $"{cur.Info.Path}/_{kind}{cur.Sub++}";
    string NewSub(string kind, List<string> cmds) { var p = ReservePath(kind); pack.Functions[p] = cmds; return p; }

    List<string> Capture(Action a)
    {
        var saved = cur.Sink; cur.Sink = new();
        try { a(); return cur.Sink; } finally { cur.Sink = saved; }
    }

    void Emit(string cmd) => cur.Sink.Add(cmd);

    // ================================================================ expression statements / assignment
    void EmitExprStatement(ExpressionSyntax e)
    {
        switch (e)
        {
            case AssignmentExpressionSyntax a: EmitAssignment(a); break;
            case PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.PreIncrementExpression or (int)SyntaxKind.PreDecrementExpression } p:
                IncDec(p.Operand, p.IsKind(SyntaxKind.PreIncrementExpression)); break;
            case PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.PostIncrementExpression or (int)SyntaxKind.PostDecrementExpression } p:
                IncDec(p.Operand, p.IsKind(SyntaxKind.PostIncrementExpression)); break;
            case InvocationExpressionSyntax inv: EmitInvocation(inv, false); break;
            default: throw new UnsupportedException(e, $"This expression cannot be used as a statement: {e.Kind()}");
        }
    }

    ScoreVar IncDec(ExpressionSyntax target, bool inc)
    {
        if (ElemTarget(target) is var (ea, ei))
            return new ScoreVar(((ScoreOp)ElemIncDec(ea, ei, inc, false, target)).Holder, ea.Elem);
        var v = GetVar(target) as ScoreVar ?? throw new UnsupportedException(target, "++/-- only works on numeric variables.");
        int step = ScaleOf(v.Kind);
        Emit($"scoreboard players {(inc ? "add" : "remove")} {v.Holder} {step}");
        return v;
    }

    Var? EmitAssignment(AssignmentExpressionSyntax a)
    {
        if (ElemTarget(a.Left) is var (la, li)) return EmitElemAssign(a, la, li);
        var sym = cur.Model.GetSymbolInfo(a.Left).Symbol;
        // Entity property
        if (sym is IPropertySymbol prop && a.Left is MemberAccessExpressionSyntax ma && Kind(prop.ContainingType) == VK.Entity)
        {
            if (!a.IsKind(SyntaxKind.SimpleAssignmentExpression))
                throw new UnsupportedException(a, "Entity properties only support plain '=' assignment.");
            EmitEntitySet(GetEnt(ma.Expression), prop, a.Right);
            return null;
        }
        if (sym is IPropertySymbol lprop && EmitLibPropertySet(lprop, a.Left as MemberAccessExpressionSyntax, a)) return null;
        var v = GetVar(a.Left);
        switch (v)
        {
            case ScoreVar sv:
                if (a.IsKind(SyntaxKind.SimpleAssignmentExpression)) AssignHolder(sv, a.Right);
                else
                {
                    var op = a.Kind() switch
                    {
                        SyntaxKind.AddAssignmentExpression => '+', SyntaxKind.SubtractAssignmentExpression => '-',
                        SyntaxKind.MultiplyAssignmentExpression => '*', SyntaxKind.DivideAssignmentExpression => '/',
                        SyntaxKind.ModuloAssignmentExpression => '%',
                        _ => throw new UnsupportedException(a, $"Unsupported assignment operator: {a.OperatorToken.Text}"),
                    };
                    ApplyOp(sv.Holder, op, EvalNum(a.Right, sv.Kind), sv.Kind);
                }
                return sv;
            case StrVar:
                if (!a.IsKind(SyntaxKind.SimpleAssignmentExpression)) throw new UnsupportedException(a, "Runtime string operations are not supported.");
                SetLocal(sym!, new StrVar(ConstString(a.Right)));
                return null;
            case EntVar:
                SetLocal(sym!, GetEnt(a.Right)); return null;
            case PosVar:
                SetLocal(sym!, new PosVar(EvalPos(a.Right))); return null;
            case ArrVar:
                if (!a.IsKind(SyntaxKind.SimpleAssignmentExpression)) throw new UnsupportedException(a, "Only '=' works on array variables.");
                if (sym is not ILocalSymbol) throw new UnsupportedException(a, "Static array fields cannot be reassigned (set their elements instead).");
                SetLocal(sym, ArrayValue(a.Right, sym.Name, ElemKind(((ILocalSymbol)sym).Type)));
                return null;
            case BlockVar or RefVar or ButtonVar or TextVar or GuiVar or ItemVar:
                if (sym is IFieldSymbol) throw new UnsupportedException(a, "Static objects cannot be reassigned (use Clone() to make another).");
                SetLocal(sym!, EvalLibObject(a.Right, Kind(cur.Model.GetTypeInfo(a.Left).Type), sym));
                if (a.Right is BaseObjectCreationExpressionSyntax || IsCloneCall(a.Right, out _)) RegisterObject(cur.Locals[sym!], sym!, a);
                return null;
        }
        throw new UnsupportedException(a, "Unsupported assignment target.");
    }

    void SetLocal(ISymbol sym, Var v)
    {
        if (cur.Locals.ContainsKey(sym)) cur.Locals[sym] = v;
        else throw new UnsupportedException(cur.Info.Decl, $"Cannot reassign {sym.Name}.");
    }

    void AssignHolder(ScoreVar v, ExpressionSyntax e)
    {
        if (v.Kind == VK.Bool) StoreCond(v.Holder, EvalCond(e));
        else SetHolder(v.Holder, EvalNum(e, v.Kind));
    }

    void SetHolder(string h, Operand op)
    {
        if (op is ConstOp c) Emit($"scoreboard players set {h} {c.Value}");
        else if (op is ScoreOp s && s.Holder != h) Emit($"scoreboard players operation {h} = {s.Holder}");
    }

    string ConstHolder(int c) { consts.Add(c); return H($"#{c}"); }

    void ApplyOp(string h, char op, Operand r, VK kind)
    {
        switch (op)
        {
            case '+' or '-':
                if (r is ConstOp c)
                {
                    int v = op == '+' ? c.Value : -c.Value;
                    if (v != 0) Emit($"scoreboard players {(v > 0 ? "add" : "remove")} {h} {Math.Abs(v)}");
                }
                else Emit($"scoreboard players operation {h} {op}= {((ScoreOp)r).Holder}");
                break;
            case '*' when kind == VK.Double:
                // Q15.16 × Q15.16 needs a 48-bit intermediate: split into 16-bit halves (__math/dmul)
                if (r is ConstOp dm && dm.Value % DScale == 0) Emit($"scoreboard players operation {h} *= {ConstHolder(dm.Value / DScale)}");
                else if (UseCompute) ComputeInt(h, DMulExpr(new ScoreOp(h), r));
                else CallMath2("dmul", h, r);
                break;
            case '/' when kind == VK.Double:
                if (r is ConstOp dd && dd.Value != 0 && dd.Value % DScale == 0) TruncOp(h, '/', new ConstOp(dd.Value / DScale));
                else CallMath2("ddiv", h, r);
                break;
            case '*':
                Emit($"scoreboard players operation {h} *= {Holder(r)}");
                if (kind == VK.Fixed) Emit($"scoreboard players operation {h} /= {ConstHolder(Scale)}");
                break;
            case '/':
                if (kind == VK.Fixed) Emit($"scoreboard players operation {h} *= {ConstHolder(Scale)}");
                TruncOp(h, '/', r);
                break;
            case '%':
                // same scale on both sides, so the C# remainder works on the raw scores for every numeric type
                TruncOp(h, '%', r);
                break;
        }
    }

    /// h = h / r or h % r with C# semantics: the quotient rounds toward zero and the remainder takes the
    /// sign of the dividend. (Scoreboard /= and %= floor instead: -7 / 2 = -4, -7 % 2 = 1.)
    void TruncOp(string h, char op, Operand r)
    {
        if (r is ConstOp c && c.Value != 0 && c.Value != int.MinValue)
        {
            int d = Math.Abs(c.Value);
            if (d == 1)
            {
                if (op == '%') Emit($"scoreboard players set {h} 0");
                else if (c.Value < 0) Emit($"scoreboard players operation {h} *= {ConstHolder(-1)}");
                return;
            }
            if (UseCompute) { ComputeTrunc(h, op, c); return; }
            // |h| op d, then put the sign back
            string s = Temp();
            Emit($"execute store success score {s} if score {h} matches ..-1");
            Emit($"execute if score {s} matches 1 run scoreboard players operation {h} *= {ConstHolder(-1)}");
            Emit($"scoreboard players operation {h} {op}= {ConstHolder(d)}");
            bool flipWhenNeg = op == '%' || c.Value > 0;   // a / -d: the sign flips when a >= 0
            Emit($"execute if score {s} matches {(flipWhenNeg ? 1 : 0)} run scoreboard players operation {h} *= {ConstHolder(-1)}");
            return;
        }
        if (UseCompute) { ComputeTrunc(h, op, r); return; }
        CallMath2(op == '/' ? "idiv" : "irem", h, r);
    }

    /// A plain scoreboard operation with a constant (for values known to be non-negative).
    void RawOp(string h, char op, int c) => Emit($"scoreboard players operation {h} {op}= {ConstHolder(c)}");

    static int? Fold(int a, char op, int b, VK kind) => op switch
    {
        '+' => a + b, '-' => a - b,
        '*' => kind switch { VK.Fixed => (int)((long)a * b / Scale), VK.Double => (int)FloorDiv((long)a * b, DScale), _ => a * b },
        '/' when b != 0 => kind switch { VK.Fixed => (int)((long)a * Scale / b), VK.Double => (int)Math.Clamp((long)a * DScale / b, int.MinValue, int.MaxValue), _ => a / b },
        '%' when b != 0 => a % b,
        _ => null,
    };

    string Holder(Operand op) => op switch { ConstOp c => ConstHolder(c.Value), ScoreOp s => s.Holder, _ => throw new InvalidOperationException() };

    string ToTemp(Operand op) { var t = Temp(); SetHolder(t, op); return t; }

    // ================================================================ numeric expressions
    Operand EvalNum(ExpressionSyntax e, VK target)
    {
        var cv = cur.Model.GetConstantValue(e);
        if (cv.HasValue) return new ConstOp(ScaleConst(cv.Value, target));
        var src = Kind(cur.Model.GetTypeInfo(e).Type);
        return Convert(EvalRaw(e, src), src, target, e);
    }

    Operand Convert(Operand op, VK from, VK to, SyntaxNode at)
    {
        if (from == to || (from == VK.Bool && to == VK.Int)) return op;
        if (from is VK.Int or VK.Bool && IsFrac(to))
        {
            if (op is ConstOp c) return new ConstOp(c.Value * ScaleOf(to));
            var t = ToTemp(op); ApplyOp(t, '*', new ConstOp(ScaleOf(to)), VK.Int); return new ScoreOp(t);
        }
        if (IsFrac(from) && to == VK.Int)
        {
            if (op is ConstOp c) return new ConstOp(c.Value / ScaleOf(from));
            var t = ToTemp(op); ApplyOp(t, '/', new ConstOp(ScaleOf(from)), VK.Int); return new ScoreOp(t);
        }
        // float (×1000) ↔ double (×65536): 65536/1000 = 8192/125
        if (from == VK.Fixed && to == VK.Double) return MulDiv(op, 8192, 125);
        if (from == VK.Double && to == VK.Fixed) return MulDiv(op, 125, 8192);
        if (to is VK.Unsupported) return op; // e.g. object (WriteLine(object))
        throw new UnsupportedException(at, $"Unsupported conversion: {from} -> {to}");
    }

    /// floor(op * num / den) without overflowing the intermediate product (num, den small).
    Operand MulDiv(Operand op, int num, int den)
    {
        if (op is ConstOp c) return new ConstOp((int)FloorDiv((long)c.Value * num, den));
        string q = ToTemp(op), r = ToTemp(new ScoreOp(q));
        Emit($"scoreboard players operation {q} /= {ConstHolder(den)}");
        Emit($"scoreboard players operation {r} %= {ConstHolder(den)}");
        Emit($"scoreboard players operation {q} *= {ConstHolder(num)}");
        Emit($"scoreboard players operation {r} *= {ConstHolder(num)}");
        Emit($"scoreboard players operation {r} /= {ConstHolder(den)}");
        Emit($"scoreboard players operation {q} += {r}");
        return new ScoreOp(q);
    }

    static int ScaleConst(object? v, VK k) => v switch
    {
        bool b => b ? 1 : 0,
        null => 0,
        _ when IsFrac(k) => (int)Math.Clamp(Math.Round(System.Convert.ToDouble(v, CultureInfo.InvariantCulture) * ScaleOf(k)), int.MinValue, int.MaxValue),
        _ => (int)System.Convert.ToDouble(v, CultureInfo.InvariantCulture),
    };

    Operand EvalRaw(ExpressionSyntax e, VK kind)
    {
        switch (e)
        {
            case ParenthesizedExpressionSyntax p: return EvalNum(p.Expression, kind);
            case IdentifierNameSyntax or MemberAccessExpressionSyntax:
            {
                var sym = cur.Model.GetSymbolInfo(e).Symbol;
                if (sym is IPropertySymbol prop && e is MemberAccessExpressionSyntax ma && Kind(prop.ContainingType) == VK.Entity)
                    return EntityGet(GetEnt(ma.Expression), prop, kind, e);
                if (sym is IPropertySymbol lprop && TryLibPropertyGet(lprop, e as MemberAccessExpressionSyntax, kind) is Operand lop)
                    return lop;
                if (sym is IPropertySymbol { Name: "Length" } && e is MemberAccessExpressionSyntax lm2 && ArrOf(lm2.Expression) is ArrVar wa)
                    return new ConstOp(wa.Length);
                if (sym is IPropertySymbol { Name: "Length" } && e is MemberAccessExpressionSyntax lm && ArrayOf(lm.Expression) is IFieldSymbol la)
                    return new ConstOp(arrays[la].Count);
                var gv = GetVar(e);
                if (gv is ConstVar cvv) return new ConstOp(cvv.Value);
                if (gv is ScoreVar sv) return new ScoreOp(sv.Holder);
                throw new UnsupportedException(e, $"Cannot use this value as a number: {e}");
            }
            case BinaryExpressionSyntax bb when kind == VK.Bool:
            {
                var bc = EvalCond(bb);
                return bc.Const is bool cb ? new ConstOp(cb ? 1 : 0) : new ScoreOp(Materialize(bc));
            }
            case BinaryExpressionSyntax b when kind != VK.Bool:
            {
                char op = b.Kind() switch
                {
                    SyntaxKind.AddExpression => '+', SyntaxKind.SubtractExpression => '-', SyntaxKind.MultiplyExpression => '*',
                    SyntaxKind.DivideExpression => '/', SyntaxKind.ModuloExpression => '%',
                    _ => throw new UnsupportedException(b, $"Unsupported operator: {b.OperatorToken.Text}"),
                };
                var lv = EvalNum(b.Left, kind);
                if (lv is ConstOp lc)
                {
                    var rv = EvalNum(b.Right, kind);
                    if (rv is ConstOp rc && Fold(lc.Value, op, rc.Value, kind) is int folded) return new ConstOp(folded);
                    var t0 = ToTemp(lc); ApplyOp(t0, op, rv, kind); return new ScoreOp(t0);
                }
                var t = ToTemp(lv);
                ApplyOp(t, op, EvalNum(b.Right, kind), kind);
                return new ScoreOp(t);
            }
            case ElementAccessExpressionSyntax wea when ElemTarget(wea) is var (ra, ri):
                return ArrGet(ra, ri, wea);
            case ElementAccessExpressionSyntax ea when ArrayOf(ea.Expression) is IFieldSymbol af:
                return ArrayElement(af, EvalNum(ea.ArgumentList.Arguments[0].Expression, VK.Int), ea);
            case PrefixUnaryExpressionSyntax u when u.IsKind(SyntaxKind.UnaryMinusExpression):
            {
                var uv = EvalNum(u.Operand, kind);
                if (uv is ConstOp uc) return new ConstOp(-uc.Value);
                var t = ToTemp(uv);
                Emit($"scoreboard players operation {t} *= {ConstHolder(-1)}");
                return new ScoreOp(t);
            }
            case PrefixUnaryExpressionSyntax u when u.IsKind(SyntaxKind.UnaryPlusExpression): return EvalNum(u.Operand, kind);
            case PrefixUnaryExpressionSyntax u when u.IsKind(SyntaxKind.PreIncrementExpression) || u.IsKind(SyntaxKind.PreDecrementExpression):
                return new ScoreOp(IncDec(u.Operand, u.IsKind(SyntaxKind.PreIncrementExpression)).Holder);
            case PostfixUnaryExpressionSyntax u when ElemTarget(u.Operand) is var (pa, pi):
                return ElemIncDec(pa, pi, u.IsKind(SyntaxKind.PostIncrementExpression), true, u);
            case PostfixUnaryExpressionSyntax u:
            {
                var v = GetVar(u.Operand) as ScoreVar ?? throw new UnsupportedException(u, "++/-- only works on numeric variables.");
                var t = ToTemp(new ScoreOp(v.Holder));
                IncDec(u.Operand, u.IsKind(SyntaxKind.PostIncrementExpression));
                return new ScoreOp(t);
            }
            case AssignmentExpressionSyntax a:
                return EmitAssignment(a) is ScoreVar asv ? new ScoreOp(asv.Holder) : throw new UnsupportedException(a, "This assignment expression cannot be used as a value.");
            case InvocationExpressionSyntax inv:
                return EmitInvocation(inv, true) ?? throw new UnsupportedException(inv, "This method does not return a value.");
            case ConditionalExpressionSyntax ce:
            {
                Cond? cc = null;
                var ccmds = Capture(() => cc = EvalCond(ce.Condition));
                if (ccmds.Count == 0 && cc!.Const is bool cb) return EvalNum(cb ? ce.WhenTrue : ce.WhenFalse, kind);
                var t = Temp();
                var ct = Materialize(EvalCond(ce.Condition));
                var whenTrue = Capture(() => SetHolder(t, EvalNum(ce.WhenTrue, kind)));
                var whenFalse = Capture(() => SetHolder(t, EvalNum(ce.WhenFalse, kind)));
                EmitGuarded($"if score {ct} matches 1", whenTrue, "if");
                EmitGuarded($"if score {ct} matches 0", whenFalse, "else");
                return new ScoreOp(t);
            }
            case CastExpressionSyntax c:
            {
                var innerKind = Kind(cur.Model.GetTypeInfo(c.Expression).Type);
                return Convert(EvalNum(c.Expression, innerKind), innerKind, kind, c);
            }
        }
        if (kind == VK.Bool) { var bc = EvalCond(e); return bc.Const is bool cb ? new ConstOp(cb ? 1 : 0) : new ScoreOp(Materialize(bc)); }
        throw new UnsupportedException(e, $"Unsupported expression: {e.Kind()}");
    }

    // ================================================================ conditions
    Cond EvalCond(ExpressionSyntax e)
    {
        var cv = cur.Model.GetConstantValue(e);
        if (cv.HasValue && cv.Value is bool cb) return Cond.Of(cb);
        switch (e)
        {
            case ParenthesizedExpressionSyntax p: return EvalCond(p.Expression);
            case PrefixUnaryExpressionSyntax u when u.IsKind(SyntaxKind.LogicalNotExpression): return Negate(EvalCond(u.Operand));
            case BinaryExpressionSyntax b when b.IsKind(SyntaxKind.LogicalAndExpression) || b.IsKind(SyntaxKind.LogicalOrExpression):
            {
                bool and = b.IsKind(SyntaxKind.LogicalAndExpression);
                var l = EvalCond(b.Left);
                if (l.Const is bool lc) { if (lc == and) return EvalCond(b.Right); return Cond.Of(lc); }
                Cond r = null!;
                var rSetup = Capture(() => r = EvalCond(b.Right));
                if (and && rSetup.Count == 0) return r.Const switch { true => l, false => Cond.Of(false), _ => new(l.Parts.Concat(r.Parts).ToList(), null) };
                // Short-circuit: evaluate the right side only when needed
                string t = Materialize(l);
                rSetup.Add(StoreCondCmd(t, r));
                EmitGuarded($"if score {t} matches {(and ? 1 : 0)}", rSetup, and ? "and" : "or");
                return Cond.Of($"if score {t} matches 1");
            }
            case BinaryExpressionSyntax b when (b.IsKind(SyntaxKind.EqualsExpression) || b.IsKind(SyntaxKind.NotEqualsExpression))
                && (b.Left.IsKind(SyntaxKind.NullLiteralExpression) || b.Right.IsKind(SyntaxKind.NullLiteralExpression)):
            {
                var refSide = b.Left.IsKind(SyntaxKind.NullLiteralExpression) ? b.Right : b.Left;
                var exists = Cond.Of($"if entity {GetRef(refSide).Selector}");
                return b.IsKind(SyntaxKind.NotEqualsExpression) ? exists : Negate(exists);
            }
            case InvocationExpressionSyntax ci when Intrinsic(cur.Model.GetSymbolInfo(ci).Symbol) == "check":
                return Cond.Of("if " + ConstStr(ci.ArgumentList.Arguments[0].Expression));
            case BinaryExpressionSyntax b when IsComparison(b):
                return Compare(b);
        }
        var op = EvalNum(e, VK.Bool);
        return op is ConstOp c ? Cond.Of(c.Value != 0) : Cond.Of($"if score {((ScoreOp)op).Holder} matches 1");
    }

    static bool IsComparison(BinaryExpressionSyntax b) => b.Kind() is SyntaxKind.LessThanExpression or SyntaxKind.LessThanOrEqualExpression
        or SyntaxKind.GreaterThanExpression or SyntaxKind.GreaterThanOrEqualExpression or SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression;

    Cond Compare(BinaryExpressionSyntax b)
    {
        if (Kind(cur.Model.GetTypeInfo(b.Left).Type) == VK.String)
        {
            string? l = TryConstString(b.Left), r = TryConstString(b.Right);
            if (l == null || r == null) throw new UnsupportedException(b, "Only compile-time strings can be compared.");
            return Cond.Of(b.IsKind(SyntaxKind.EqualsExpression) ? l == r : l != r);
        }
        var k = Kind(cur.Model.GetTypeInfo(b.Left).ConvertedType);
        if (k is not (VK.Int or VK.Fixed or VK.Double or VK.Bool)) throw new UnsupportedException(b, $"Comparisons are only supported between numbers/bools ({k}).");
        var L = EvalNum(b.Left, k);
        if (L is ScoreOp && !IsPure(b.Right)) L = new ScoreOp(ToTemp(L));
        var R = EvalNum(b.Right, k);
        string op = b.OperatorToken.Text;
        if (L is ConstOp && R is ScoreOp)
        {
            (L, R) = (R, L);
            op = op switch { "<" => ">", ">" => "<", "<=" => ">=", ">=" => "<=", _ => op };
        }
        if (L is ConstOp lc2 && R is ConstOp rc2) // both constant at runtime (rare)
            return Cond.Of(op switch { "<" => lc2.Value < rc2.Value, "<=" => lc2.Value <= rc2.Value, ">" => lc2.Value > rc2.Value, ">=" => lc2.Value >= rc2.Value, "==" => lc2.Value == rc2.Value, _ => lc2.Value != rc2.Value });
        string lh = ((ScoreOp)L).Holder;
        if (R is ConstOp c)
        {
            int v = c.Value;
            return op switch
            {
                "<" => Cond.Of($"if score {lh} matches ..{v - 1}"),
                "<=" => Cond.Of($"if score {lh} matches ..{v}"),
                ">" => Cond.Of($"if score {lh} matches {v + 1}.."),
                ">=" => Cond.Of($"if score {lh} matches {v}.."),
                "==" => Cond.Of($"if score {lh} matches {v}"),
                _ => Cond.Of($"unless score {lh} matches {v}"),
            };
        }
        string rh = ((ScoreOp)R).Holder;
        return op switch
        {
            "==" => Cond.Of($"if score {lh} = {rh}"),
            "!=" => Cond.Of($"unless score {lh} = {rh}"),
            _ => Cond.Of($"if score {lh} {op} {rh}"),
        };
    }

    static bool IsPure(ExpressionSyntax e) => !e.DescendantNodesAndSelf().Any(n =>
        n is InvocationExpressionSyntax or AssignmentExpressionSyntax or PostfixUnaryExpressionSyntax
        || n.IsKind(SyntaxKind.PreIncrementExpression) || n.IsKind(SyntaxKind.PreDecrementExpression));

    Cond Negate(Cond c)
    {
        if (c.Const is bool b) return Cond.Of(!b);
        if (c.Parts.Count == 1)
        {
            var p = c.Parts[0];
            return Cond.Of(p.StartsWith("if ") ? "unless " + p[3..] : "if " + p[7..]);
        }
        return Cond.Of($"if score {Materialize(c)} matches 0");
    }

    string Materialize(Cond c) { var t = Temp(); StoreCond(t, c); return t; }
    void StoreCond(string h, Cond c) => Emit(StoreCondCmd(h, c));
    string StoreCondCmd(string h, Cond c) => c.Const is bool b
        ? $"scoreboard players set {h} {(b ? 1 : 0)}"
        : $"execute store success score {h} {string.Join(" ", c.Parts)}";

    // ================================================================ variables
    Var GetVar(ExpressionSyntax e)
    {
        if (ElemTarget(e) is var (ga, gi))
            return gi is ConstOp gc ? new ScoreVar(ElemHolder(ga, gc.Value, e), ga.Elem) : throw new UnsupportedException(e, "A runtime array index cannot be used here.");
        if (e is ThisExpressionSyntax) return ThisRef(e);
        var sym = cur.Model.GetSymbolInfo(e).Symbol;
        if (sym == null) throw new UnsupportedException(e, $"Unknown symbol: {e}");
        if (cur.Locals.TryGetValue(sym, out var v)) return v;
        if (sym is IFieldSymbol { IsStatic: false } ef && IsEntityClass(ef.ContainingType))
        {
            var recv = e is MemberAccessExpressionSyntax ma ? GetRef(ma.Expression) : ThisRef(e);
            return new ScoreVar(FieldHolder(recv, ef), Kind(ef.Type));
        }
        if (sym is IFieldSymbol f && statics.TryGetValue(f, out var sv)) return sv;
        if (sym is IFieldSymbol gf && guiStatics.TryGetValue(gf, out var gv)) return gv;
        if (sym is IFieldSymbol af2 && arrFields.TryGetValue(af2, out var arv)) return arv;
        if (sym is IFieldSymbol itf && itemStatics.TryGetValue(itf, out var itv)) return itv;
        if (sym is ILocalSymbol) throw new UnsupportedException(e, ""); // its declaration already failed (error reported there)
        if (sym is IFieldSymbol sf && arrays.ContainsKey(sf)) throw new UnsupportedException(e, $"Use {sf.Name}[index] (arrays are compile-time tables).");
        throw new UnsupportedException(e, $"Cannot use '{sym.Name}' as a variable here.");
    }

    EntVar GetEnt(ExpressionSyntax e)
    {
        if (IsCloneCall(e, out var src)) return CloneEntity(GetEnt(src), EntityTag("clone"));   // a temporary object
        var ev = GetVar(e) as EntVar ?? throw new UnsupportedException(e, "Expected an Entity variable.");
        CheckNotDisposed(ev, e);
        return ev;
    }

    string Sel(EntVar ev) => $"@e[tag={ev.Tag},limit=1]";

    string? ConstString(ExpressionSyntax e)
    {
        var cv = cur.Model.GetConstantValue(e);
        if (cv.HasValue) return cv.Value as string;
        if (TryConstString(e) is string cs) return cs;
        var sym = cur.Model.GetSymbolInfo(e).Symbol;
        if (sym != null && cur.Locals.TryGetValue(sym, out var v) && v is StrVar s) return s.Value;
        throw new UnsupportedException(e, "Strings must be compile-time constants (runtime string operations are not supported).");
    }

    // ================================================================ calls
    static string? Intrinsic(ISymbol? s) => s?.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == "IntrinsicAttribute")?.ConstructorArguments[0].Value as string;
    static bool HasAttr(ISymbol s, string name) => s.GetAttributes().Any(a => a.AttributeClass?.Name == name);

    Operand? EmitInvocation(InvocationExpressionSyntax inv, bool want)
    {
        var sym = cur.Model.GetSymbolInfo(inv).Symbol as IMethodSymbol
            ?? throw new UnsupportedException(inv, $"Cannot resolve method: {inv.Expression}");
        var intr = Intrinsic(sym);
        if (intr != null) return EmitIntrinsic(intr, inv);
        if (HasAttr(sym, "InlineAttribute")) return EmitInline(sym, inv, want, out _);

        if (!fns.TryGetValue(sym.OriginalDefinition, out var callee))
            throw new UnsupportedException(inv, $"'{sym.ContainingType?.Name}.{sym.Name}' cannot be used in MC# (not available inside Minecraft).");
        var args = inv.ArgumentList.Arguments;
        for (int i = 0; i < args.Count; i++)
        {
            var p = sym.Parameters[i];
            switch (callee.Params[p.OriginalDefinition])
            {
                case ScoreVar sv: AssignHolder(sv, args[i].Expression); break;
                case EntVar ev:
                    var src = GetEnt(args[i].Expression);
                    Emit($"tag @e[tag={ev.Tag}] remove {ev.Tag}");
                    Emit($"tag {Sel(src)} add {ev.Tag}");
                    break;
                case RefVar pv:
                    Emit($"tag @a remove {pv.Tag}");
                    Emit($"tag {GetRef(args[i].Expression).Selector} add {pv.Tag}");
                    break;
            }
        }
        if (sym.IsStatic) Emit($"function {callee.Path}");
        else
        {
            var recv = inv.Expression is MemberAccessExpressionSyntax rma ? GetRef(rma.Expression) : ThisRef(inv);
            Emit(recv.Selector == "@s" ? $"function {callee.Path}" : $"execute as {recv.Selector} at @s run function {callee.Path}");
        }
        if (!want || sym.ReturnsVoid || Kind(sym.ReturnType) == VK.Ref) return null;
        return new ScoreOp(ToTemp(new ScoreOp(callee.Ret)));
    }

    Operand? EmitIntrinsic(string name, InvocationExpressionSyntax inv)
    {
        var args = inv.ArgumentList.Arguments;
        switch (name)
        {
            case "asm":
            {
                var text = ConstString(args[0].Expression) ?? "";
                foreach (var line in text.Split('\n'))
                {
                    var l = line.Trim();
                    if (l.Length == 0) continue;
                    Emit(l.StartsWith('/') ? l[1..] : l);
                }
                return null;
            }
            case "console.writeline":
            {
                var comps = args.Count == 0 ? new List<string>() : Components(args[0].Expression);
                Emit($"tellraw @a [\"\"{string.Concat(comps.Select(c => "," + c))}]");
                return null;
            }
            case "spawn":
            {
                var ev = GetEnt(args[0].Expression);
                if (ev.TypeId == null) throw new UnsupportedException(args[0], "Unknown entity type. Create it with 'new Entity : Type x;'.");
                var pos = EvalPos(args[1].Expression);
                string t = Temp();
                Emit($"scoreboard players set {t} 0"); // stays 0 if the position entity is missing (execute doesn't run)
                Consume(args[0].Expression, ev, inv, () =>
                {
                    Emit($"tag @e[tag={ev.Tag}] remove {ev.Tag}");
                    EmitCmd($"execute store success score {t} {pos.ExecPrefix}run summon {ev.TypeId} {pos.Coords} {{Tags:[\"{ev.Tag}\"]}}", inv, pos);
                    EmitGuarded($"if score {t} matches 1", new List<string>(ev.Deferred), "init");
                });
                ev.Spawned = true;
                return new ScoreOp(t);
            }
            case "entity.kill":
                Emit($"kill {Sel(Receiver(inv))}");
                return null;
            case "display.animate":
                EmitAnimate(Receiver(inv), args[0].Expression);
                return null;
            case "entity.tp":
            {
                var ev = Receiver(inv);
                var tpos = EvalPos(args[0].Expression);
                EmitCmd(At(tpos, c => $"tp {Sel(ev)} {c}"), inv, tpos);
                return null;
            }
        }
        if (name.StartsWith("math.")) return EmitMath(name, inv);
        if (name == "item.give") return EmitGive(inv);
        if (name == "obj.dispose") { EmitDispose(inv); return null; }
        if (name == "obj.clone") throw new UnsupportedException(inv, "Clone() makes a new object: assign it to a variable or pass it directly (m.GiveObject(wand.Clone(), p)).");
        if (name == "mount") return new ConstOp(1);   // bound at compile time (CollectMounts)
        if (name == "wait")
            throw new UnsupportedException(inv, "Wait() can only be used as a statement directly in a method body or inside if/else, while, for or do-while (not in switch, foreach, lambdas or expressions).");
        return EmitLibIntrinsic(name, inv);
    }

    EntVar Receiver(InvocationExpressionSyntax inv) =>
        inv.Expression is MemberAccessExpressionSyntax ma ? GetEnt(ma.Expression) : throw new UnsupportedException(inv, "No entity receiver.");

    /// Turns a Position expression into a compile-time position (execute context + coordinates).
    Pos EvalPos(ExpressionSyntax e)
    {
        var sym = cur.Model.GetSymbolInfo(e).Symbol;
        switch (Intrinsic(sym))
        {
            case "worldspawn":
                usesWorldSpawn = true;
                return new Pos($"at @e[type=minecraft:marker,tag={WorldSpawnTag},limit=1]", "~", "~", "~");
            case "pos.here": return Pos.Here;
            case "entity.pos": return new Pos($"at {Sel(GetEnt(((MemberAccessExpressionSyntax)e).Expression))}", "~", "~", "~");
            case "ent.pos": return new Pos($"at {(e is MemberAccessExpressionSyntax pm ? GetRef(pm.Expression) : ThisRef(e)).Selector}", "~", "~", "~");
            case "player.pos": return new Pos($"at {SelWith(GetRef(((MemberAccessExpressionSyntax)e).Expression).Selector, "limit=1")}", "~", "~", "~");
            case "pos.new":
            {
                var args = ((BaseObjectCreationExpressionSyntax)e).ArgumentList!.Arguments;
                var coords = new string[3];
                var margs = new List<(string, Operand, VK)>();
                for (int i = 0; i < 3; i++)
                {
                    var op = EvalNum(args[i].Expression, VK.Fixed);
                    if (op is ConstOp c) coords[i] = Fmt(c.Value / (double)Scale);
                    else { string n = $"p{refCounter++}"; margs.Add((n, op, VK.Fixed)); coords[i] = $"$({n})"; }
                }
                return new Pos("", coords[0], coords[1], coords[2]) { Args = margs.ToArray() };
            }
            case "pos.offset":
            {
                var inv = (InvocationExpressionSyntax)e;
                var basePos = EvalPos(((MemberAccessExpressionSyntax)inv.Expression).Expression);
                var d = inv.ArgumentList.Arguments.Select(a => ConstDouble(a.Expression, "Offset")).ToArray();
                return basePos.Offset(d[0], d[1], d[2]);
            }
        }
        if (e is ParenthesizedExpressionSyntax p) return EvalPos(p.Expression);
        if (GetVar(e) is PosVar pv) return pv.Pos;
        throw new UnsupportedException(e, "Unsupported Position expression.");
    }

    double ConstDouble(ExpressionSyntax e, string what)
    {
        var cv = cur.Model.GetConstantValue(e);
        if (cv.HasValue) return System.Convert.ToDouble(cv.Value, CultureInfo.InvariantCulture);
        Operand? op = null;
        var cmds = Capture(() => op = EvalNum(e, VK.Fixed));
        if (cmds.Count == 0 && op is ConstOp c) return c.Value / (double)Scale;
        throw new UnsupportedException(e, $"{what} must be compile-time constants.");
    }

    static string Fmt(double d) => d.ToString("0.######", CultureInfo.InvariantCulture);

    /// "execute <context> run <cmd>" (just the command if there is no context)
    static string At(Pos p, Func<string, string> cmd) => p.Prefix.Length > 0 ? $"execute {p.Prefix} run {cmd(p.Coords)}" : cmd(p.Coords);

    // ================================================================ entity properties
    (string? nbtPath, string? nbtType, string? attr) PropInfo(IPropertySymbol p)
    {
        foreach (var a in p.GetAttributes())
        {
            if (a.AttributeClass?.Name == "NbtAttribute") return ((string)a.ConstructorArguments[0].Value!, (string)a.ConstructorArguments[1].Value!, null);
            if (a.AttributeClass?.Name == "McAttributeAttribute") return (null, null, $"minecraft:{prof.AttributePrefix}{a.ConstructorArguments[0].Value}");
        }
        return (null, null, null);
    }

    void EmitEntitySet(EntVar ev, IPropertySymbol p, ExpressionSyntax value)
    {
        CheckSince(p, value);
        var (path, type, attr) = PropInfo(p);
        if (path != null && IsDisplayNbtType(type!)) { EmitDisplaySet(ev, p, path, type!, value); return; }
        var kind = Kind(p.Type);
        var cv = cur.Model.GetConstantValue(value);
        string cmd;
        if (path != null)
        {
            if (p.SetMethod == null || type is "int[]" or "string") throw new UnsupportedException(value, $"{p.Name} is read-only.");
            if (cv.HasValue) cmd = $"data modify entity {Sel(ev)} {path} set value {NbtLiteral(cv.Value!, type!)}";
            else
            {
                string h = ev.Spawned ? Holder(EvalNum(value, kind)) : StableCopy(ev, p, value, kind);
                cmd = $"execute store result entity {Sel(ev)} {path} {type} {InvScale(kind)} run scoreboard players get {h}";
            }
        }
        else if (attr != null)
        {
            if (cv.HasValue) cmd = $"attribute {Sel(ev)} {attr} base set {FormatNum(cv.Value!)}";
            else
            {
                // Runtime value: pass it through a function macro (1.20.2+)
                if (!prof.Macros)
                    throw new UnsupportedException(value, $"Setting {p.Name} from a runtime value needs function macros (1.20.2+). Use '#if MC_1_20_2_OR_GREATER' or raise the minimum target version.");
                string h = ev.Spawned ? Holder(EvalNum(value, kind)) : StableCopy(ev, p, value, kind);
                string macro = $"{ns}:__macro/m{macroCounter++}";
                pack.Functions[macro] = new() { $"$attribute {Sel(ev)} {attr} base set $(v)" };
                var cmds = new[]
                {
                    $"execute store result storage {Storage} arg.v double {InvScale(kind)} run scoreboard players get {h}",
                    $"function {macro} with storage {Storage} arg",
                };
                if (ev.Spawned) cur.Sink.AddRange(cmds); else ev.Deferred.AddRange(cmds);
                return;
            }
        }
        else throw new UnsupportedException(value, $"{p.Name} cannot be set.");

        if (ev.Spawned) Emit(cmd); else ev.Deferred.Add(cmd);
    }

    /// Writes to a not-yet-spawned entity are applied after summon, so copy the current value into a dedicated holder.
    string StableCopy(EntVar ev, IPropertySymbol p, ExpressionSyntax value, VK kind)
    {
        string h = H($"${ev.Tag}.{p.Name}");
        if (kind == VK.Bool) StoreCond(h, EvalCond(value)); else SetHolder(h, EvalNum(value, kind));
        return h;
    }

    Operand EntityGet(EntVar ev, IPropertySymbol p, VK kind, SyntaxNode at)
    {
        CheckSince(p, at);
        var (path, type, attr) = PropInfo(p);
        if (type == "brightness") path += ".block";
        else if (type == "scale3") path += "[0]";
        else if (type != null && IsDisplayNbtType(type)) throw new UnsupportedException(at, $"{p.Name} cannot be read.");
        string scale = ScaleOf(kind).ToString();
        string t = Temp();
        if (path != null && type is not ("int[]" or "string"))
            Emit($"execute store result score {t} run data get entity {Sel(ev)} {path} {scale}");
        else if (attr != null)
            Emit($"execute store result score {t} run attribute {Sel(ev)} {attr} get {scale}");
        else throw new UnsupportedException(at, $"{p.Name} cannot be used as a number.");
        return new ScoreOp(t);
    }

    static string NbtLiteral(object v, string type)
    {
        string n = FormatNum(v);
        if (type == "byte" && v is int iv && iv is > 127 and < 256) n = (iv - 256).ToString();   // 0–255 → signed byte
        return type switch { "byte" => (v is bool b ? (b ? "1" : "0") : n) + "b", "short" => n + "s", "long" => n + "L", "float" => n + "f", "double" => n + "d", _ => n };
    }

    static string FormatNum(object v) => v switch
    {
        bool b => b ? "1" : "0",
        float f => f.ToString("0.######", CultureInfo.InvariantCulture),
        double d => d.ToString("0.######", CultureInfo.InvariantCulture),
        _ => System.Convert.ToString(v, CultureInfo.InvariantCulture)!,
    };

    // ================================================================ text components (Console.WriteLine)
    static string Text(string s) => $"{{\"text\":{Json.Str(s)}}}";

    List<string> Components(ExpressionSyntax e)
    {
        var cv = cur.Model.GetConstantValue(e);
        if (cv.HasValue) return new() { Text(cv.Value switch { null => "", bool b => b ? "True" : "False", IFormattable f => f.ToString(null, CultureInfo.InvariantCulture), var o => o.ToString()! }) };

        switch (e)
        {
            case ParenthesizedExpressionSyntax p: return Components(p.Expression);
            case ElementAccessExpressionSyntax sea when ArrayOf(sea.Expression) is IFieldSymbol stf
                && Kind(((IArrayTypeSymbol)stf.Type).ElementType) == VK.String && TryConstIndex(sea) == null:
            {
                // string table with a runtime index: copy the element to storage and show it as an nbt component
                string path = $"s{storageCounter++}";
                CopyElement(stf, EvalNum(sea.ArgumentList.Arguments[0].Expression, VK.Int), path, sea);
                return new() { $"{{\"nbt\":\"{path}\",\"storage\":{Json.Str(Storage)}}}" };
            }
            case InterpolatedStringExpressionSyntax istr:
                return istr.Contents.SelectMany(c => c switch
                {
                    InterpolatedStringTextSyntax t => new List<string> { Text(Unbrace(t.TextToken.ValueText)) },
                    InterpolationSyntax i => Components(i.Expression),
                    _ => new List<string>(),
                }).ToList();
            case BinaryExpressionSyntax b when b.IsKind(SyntaxKind.AddExpression) && Kind(cur.Model.GetTypeInfo(b).Type) == VK.String:
                return Components(b.Left).Concat(Components(b.Right)).ToList();
            case InvocationExpressionSyntax inv:
            {
                var m = cur.Model.GetSymbolInfo(inv).Symbol as IMethodSymbol;
                if (m is { Name: "Join", ContainingType.SpecialType: SpecialType.System_String } && inv.ArgumentList.Arguments.Count == 2)
                {
                    string sep = ConstString(inv.ArgumentList.Arguments[0].Expression) ?? "";
                    if (ArrOf(inv.ArgumentList.Arguments[1].Expression) is ArrVar ja) return ArrayComps(ja, sep, inv);
                    var (sel, path) = NbtRef(inv.ArgumentList.Arguments[1].Expression);
                    return new() { $"{{\"nbt\":{Json.Str(path + "[]")},\"entity\":{Json.Str(sel)},\"separator\":{Json.Str(sep)}}}" };
                }
                if (m is { Name: "ToString" } && inv.Expression is MemberAccessExpressionSyntax tsm) return Components(tsm.Expression);
                break;
            }
            case MemberAccessExpressionSyntax ma when cur.Model.GetSymbolInfo(ma).Symbol is IPropertySymbol prop && Kind(prop.ContainingType) == VK.Entity:
            {
                var (path, type, _) = PropInfo(prop);
                if (type is "int[]" or "string")
                {
                    var (sel, p) = NbtRef(ma);
                    return new() { $"{{\"nbt\":{Json.Str(p)},\"entity\":{Json.Str(sel)}}}" };
                }
                break;
            }
        }

        if (LibComponents(e) is List<string> libComps) return libComps;
        var k = Kind(cur.Model.GetTypeInfo(e).Type);
        switch (k)
        {
            case VK.String: return new() { Text(ConstString(e) ?? "") };
            case VK.Entity: return new() { $"{{\"selector\":{Json.Str(Sel(GetEnt(e)))}}}" };
            case VK.Int: return new() { ScoreComp(Holder(EvalNum(e, VK.Int))) };
            case VK.Bool: return BoolComps(EvalCond(e));
            case VK.Fixed or VK.Double: return FixedComps(EvalNum(e, k), k);
            case VK.Array: return [Text("["), .. ArrayComps(GetArr(e), ", ", e), Text("]")];
        }
        throw new UnsupportedException(e, "This value cannot be printed.");
    }

    (string sel, string path) NbtRef(ExpressionSyntax e)
    {
        if (e is MemberAccessExpressionSyntax ma && cur.Model.GetSymbolInfo(ma).Symbol is IPropertySymbol prop && PropInfo(prop).nbtPath is string path)
            return (Sel(GetEnt(ma.Expression)), path);
        throw new UnsupportedException(e, "Only entity NBT properties are supported here (e.g. x.uuid).");
    }

    /// h = "name objective"
    static string ScoreComp(string h)
    {
        int sp = h.LastIndexOf(' ');
        return $"{{\"score\":{{\"name\":{Json.Str(h[..sp])},\"objective\":{Json.Str(h[(sp + 1)..])}}}}}";
    }

    List<string> BoolComps(Cond c)
    {
        if (c.Const is bool b) return new() { Text(b ? "True" : "False") };
        string path = $"s{storageCounter++}";
        Emit($"data modify storage {Storage} {path} set value \"False\"");
        Emit($"execute {string.Join(" ", c.Parts)} run data modify storage {Storage} {path} set value \"True\"");
        return new() { $"{{\"nbt\":\"{path}\",\"storage\":{Json.Str(Storage)}}}" };
    }

    /// Prints a fixed-point number as "-12.345" (float) or "-12.34567" (double).
    List<string> FixedComps(Operand op, VK kind)
    {
        int scale = ScaleOf(kind), digits = kind == VK.Double ? 5 : 3;
        if (op is ConstOp c) return new() { Text((c.Value / (double)scale).ToString("0." + new string('#', digits), CultureInfo.InvariantCulture)) };
        string t = ToTemp(op), sign = $"s{storageCounter++}";
        Emit($"data modify storage {Storage} {sign} set value \"\"");
        Emit($"execute if score {t} matches ..-1 run data modify storage {Storage} {sign} set value \"-\"");
        Emit($"execute if score {t} matches ..-1 run scoreboard players operation {t} *= {ConstHolder(-1)}");
        string ip = ToTemp(new ScoreOp(t)); RawOp(ip, '/', scale);
        // fraction as a decimal integer in [0, 10^digits)
        string fr = t;   // float: the digits come straight from t (×1000)
        if (kind == VK.Double)
        {
            fr = ToTemp(new ScoreOp(t)); RawOp(fr, '%', scale);
            RawOp(fr, '*', 3125); RawOp(fr, '/', 2048); // ×100000/65536
        }
        var comps = new List<string> { $"{{\"nbt\":\"{sign}\",\"storage\":{Json.Str(Storage)}}}", ScoreComp(ip), Text(".") };
        for (int p = digits - 1; p >= 0; p--)
        {
            string d = ToTemp(new ScoreOp(fr));
            if (p > 0) RawOp(d, '/', (int)Math.Pow(10, p));
            RawOp(d, '%', 10);
            comps.Add(ScoreComp(d));
        }
        return comps;
    }

    // ================================================================ utilities
    public static VK Kind(ITypeSymbol? t)
    {
        if (t == null) return VK.Unsupported;
        if (t is IArrayTypeSymbol { Rank: 1 } arr && Kind(arr.ElementType) is VK.Int or VK.Bool or VK.Fixed or VK.Double) return VK.Array;
        switch (t.SpecialType)
        {
            case SpecialType.System_Int32 or SpecialType.System_Int16 or SpecialType.System_Byte or SpecialType.System_SByte
                or SpecialType.System_UInt16 or SpecialType.System_Int64 or SpecialType.System_Char: return VK.Int;
            case SpecialType.System_Boolean: return VK.Bool;
            case SpecialType.System_Single: return VK.Fixed;
            case SpecialType.System_Double: return VK.Double;
            case SpecialType.System_String: return VK.String;
            case SpecialType.System_Void: return VK.Void;
        }
        if (t.TypeKind == TypeKind.Enum) return VK.Int;
        string? nsName = t.ContainingNamespace?.ToDisplayString();
        if (IsEntityType(t)) return VK.Entity;
        if (t.Name == "Position" && nsName == "MCS") return VK.Position;
        if (t.Name == "Button" && nsName == "TuiLib") return VK.Button;
        if (t.Name == "Text" && nsName == "TuiLib") return VK.Text;
        if (t.Name is "Board" or "BossBar" && nsName == "GuiLib") return VK.Gui;
        if (t.Name == "Item" && nsName == "ItemLib") return VK.Item;
        for (var bt = t as INamedTypeSymbol; bt != null; bt = bt.BaseType)
        {
            if (bt.Name == "Block" && bt.ContainingNamespace?.ToDisplayString() == "BlockLib") return VK.Block;
            if (bt.Name == "EntityObject" && bt.ContainingNamespace?.ToDisplayString() == "MCS") return VK.Ref;
        }
        return VK.Unsupported;
    }

    string MethodPrefix(IMethodSymbol m)
    {
        string name = Snake(m.Name);
        return m.ContainingType.Name == Preprocessor.GlobalClass ? name : Snake(m.ContainingType.Name) + "." + name;
    }

    public static string Snake(string s)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (char.IsUpper(c) && i > 0 && (char.IsLower(s[i - 1]) || char.IsDigit(s[i - 1]) || (i + 1 < s.Length && char.IsLower(s[i + 1]) && char.IsUpper(s[i - 1]))))
                sb.Append('_');
            sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_');
        }
        return sb.ToString();
    }

    static T Unsup<T>(SyntaxNode n, string msg) => throw new UnsupportedException(n, msg);

    void Error(SyntaxNode n, string msg) => Error(n.GetLocation(), msg);
    void Error(SyntaxToken t, string msg) => Error(t.GetLocation(), msg);
    void Error(Location loc, string msg)
    {
        if (msg.Length == 0) return;
        var span = loc.GetMappedLineSpan();
        Diags.Add(new Diag(span.Path, span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1, msg));
    }
}
