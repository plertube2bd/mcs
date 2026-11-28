using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Mcs.Compiler;

/// Compile-time integer (unrolled loop variables, constant arguments of [Inline] methods).
/// A compile-time number (an [Inline] parameter, an unrolled loop variable). Value is the score value (×1000 for
/// Milli, ×65536 for Q16); Text is how it is written into commands ($"…{v}…").
sealed class ConstVar(int value, VK kind = VK.Int, string? text = null) : Var
{
    public int Value = value;
    public VK Kind = kind;
    public string Text => text ?? Codegen.FormatConst(Value, Kind);
}

public sealed partial class Codegen
{
    sealed record EntityClass(string Type, string? Tag)
    {
        public bool IsPlayer => Type == "minecraft:player";
        public string BaseSelector => IsPlayer ? (Tag == null ? "@a" : $"@a[tag={Tag}]") : (Tag == null ? $"@e[type={Type}]" : $"@e[type={Type},tag={Tag}]");
    }

    readonly Dictionary<string, (string criteria, string? display, string? color)> objectives = new();
    readonly Dictionary<IFieldSymbol, List<object?>> arrays = new(SymbolEqualityComparer.Default);
    readonly HashSet<IFieldSymbol> runtimeArrays = new(SymbolEqualityComparer.Default);
    int refCounter, inlineDepth;

    /// <summary>The inside of an SNBT compound: removes exactly one outer <c>{ }</c> pair (nested braces stay).</summary>
    static string SnbtBody(string snbt)
    {
        string s = snbt.Trim();
        if (s.StartsWith('{') && s.EndsWith('}')) s = s[1..^1];
        return s.Trim();
    }

    // ================================================================ entity classes
    static EntityClass? ClassOf(ITypeSymbol? t)
    {
        for (var bt = t as INamedTypeSymbol; bt != null; bt = bt.BaseType)
        {
            var a = bt.GetAttributes().FirstOrDefault(x => x.AttributeClass?.Name == "EntityAttribute");
            if (a != null) return new EntityClass((string)a.ConstructorArguments[0].Value!, a.ConstructorArguments.Length > 1 ? a.ConstructorArguments[1].Value as string : null);
        }
        return null;
    }

    static bool IsEntityClass(ITypeSymbol? t) => ClassOf(t) != null;

    string ObjectiveOf(IFieldSymbol f)
    {
        var a = f.GetAttributes().FirstOrDefault(x => x.AttributeClass?.Name == "ScoreAttribute");
        string name = a?.ConstructorArguments.FirstOrDefault().Value as string ?? $"{ns}.{Snake(f.Name)}";
        string Named(string key) => a?.NamedArguments.FirstOrDefault(n => n.Key == key).Value.Value as string ?? "";
        if (!objectives.ContainsKey(name) || objectives[name] == ("dummy", null, null))
            objectives[name] = (Named("Criteria") is { Length: > 0 } c ? c : "dummy", Named("Display") is { Length: > 0 } d ? d : null, Named("Color") is { Length: > 0 } col ? col : null);
        return name;
    }

    /// "selector objective" holder of an entity field
    string FieldHolder(RefVar r, IFieldSymbol f)
    {
        string sel = Resolve(r).Selector;
        // A score holder must be a single entity
        if ((sel.StartsWith("@e") || sel.StartsWith("@a")) && !sel.Contains("limit=")) sel = SelWith(sel, "limit=1");
        return $"{sel} {ObjectiveOf(f)}";
    }

    RefVar Resolve(RefVar r)
    {
        if (r.Depth < 0 || r.Depth == cur.Depth) return r;
        if (r.Tag == null) throw new UnsupportedException(cur.Info.Decl, "Internal: entity reference used from a nested context without a tag.");
        return new RefVar($"@e[tag={r.Tag},limit=1]") { Tag = r.Tag, Type = r.Type };
    }

    RefVar ThisRef(SyntaxNode at) => cur.This ?? throw new UnsupportedException(at, "'this' is only available in instance methods of entity classes.");

    /// Entity references from expressions (queries, this, method results). Returns null if not one of these forms.
    RefVar? EvalRefExpr(ExpressionSyntax e)
    {
        if (e is ThisExpressionSyntax) return ThisRef(e);
        if (e is not InvocationExpressionSyntax inv) return null;
        var m = cur.Model.GetSymbolInfo(inv).Symbol as IMethodSymbol;
        if (m == null) return null;
        var args = inv.ArgumentList.Arguments;
        string Filter(int i) => args.Count > i ? ConstStr(args[i].Expression) : "";
        var t = m.TypeArguments.FirstOrDefault() ?? m.ReturnType;
        var cls = ClassOf(t);
        switch (Intrinsic(m))
        {
            case "ents.self": return new RefVar("@s") { Depth = cur.Depth, Type = t };
            case "ents.uuid": return new RefVar(ConstStr(args[0].Expression)) { Type = t };
            case "ents.first": return CaptureRef(t, "", SelWith(cls!.BaseSelector, Join(Filter(0), "limit=1")));
            case "ents.random": return CaptureRef(t, "", SelWith(cls!.BaseSelector, Join(Filter(0), "sort=random,limit=1")));
            case "ents.nearest":
            {
                var pos = EvalPos(args[0].Expression);
                double d = args.Count > 1 ? ConstDouble(args[1].Expression, "maxDistance") : -1;
                string f = Join(d >= 0 ? $"distance=..{Fmt(d)}" : "", Join(Filter(2), "sort=nearest,limit=1"));
                return CaptureRef(t, PosContext(pos), SelWith(cls!.BaseSelector, f), pos);
            }
            case "ents.spawn":
            {
                if (cls!.IsPlayer) throw new UnsupportedException(e, "Players cannot be spawned.");
                var pos = EvalPos(args[0].Expression);
                string nbt = Filter(1), c = NewRefTag();
                string tags = string.Join(",", new[] { cls.Tag, c }.Where(x => x != null).Select(x => $"\"{x}\""));
                Emit($"tag @e[tag={c}] remove {c}");
                EmitCmd(At(pos, co => $"summon {cls.Type} {co} {{Tags:[{tags}]{(nbt.Length > 0 ? "," + SnbtBody(nbt) : "")}}}"), inv, pos);
                return new RefVar($"@e[tag={c},limit=1]") { Tag = c, Type = t };
            }
        }
        if (fns.TryGetValue(m.OriginalDefinition, out var callee) && Kind(m.ReturnType) == VK.Ref)
        {
            EmitInvocation(inv, false);
            string c = NewRefTag();
            Emit($"tag @e[tag={c}] remove {c}");
            Emit($"tag @e[tag={RetTag(callee)}] add {c}");
            return new RefVar($"@e[tag={c},limit=1]") { Tag = c, Type = t };
        }
        return null;
    }

    /// Text of an interpolated string part ("{{" / "}}" are escapes for literal braces)
    static string Unbrace(string s) => s.Replace("{{", "{").Replace("}}", "}");

    string NewRefTag() => $"mcs.{ns}.r{refCounter++}";
    string RetTag(FnInfo f) => $"mcs.{ns}.ret.{f.Prefix}";
    static string Join(string a, string b) => a.Length == 0 ? b : b.Length == 0 ? a : a + "," + b;

    RefVar CaptureRef(ITypeSymbol t, string context, string selector, params Pos[] ps)
    {
        string c = NewRefTag();
        Emit($"tag @e[tag={c}] remove {c}");
        EmitCmd(context.Length > 0 ? $"execute {context} as {selector} run tag @s add {c}" : $"tag {selector} add {c}", cur.Info.Decl, ps);
        return new RefVar($"@e[tag={c},limit=1]") { Tag = c, Type = t };
    }

    /// Execute context that puts the position at p: "at ... positioned X Y Z"
    static string PosContext(Pos p) => string.Join(" ", new[] { p.Prefix, p.Coords == "~ ~ ~" ? "" : $"positioned {p.Coords}" }.Where(x => x.Length > 0));

    /// Emits a command; if a position has runtime coordinates, it goes through a macro.
    void EmitCmd(string cmd, SyntaxNode at, params Pos[] ps)
    {
        var args = ps.SelectMany(p => p.Args).ToArray();
        if (args.Length == 0) Emit(cmd); else cur.Sink.AddRange(MacroCmd(cmd, at, args));
    }

    // ================================================================ entity intrinsics
    Operand? EmitEntityIntrinsic(string name, InvocationExpressionSyntax inv)
    {
        var args = inv.ArgumentList.Arguments;
        RefVar Recv() => inv.Expression is MemberAccessExpressionSyntax ma ? GetRef(ma.Expression) : ThisRef(inv);
        Operand Store(string mode, string tail) { var t = Temp(); Emit($"scoreboard players set {t} 0"); Emit($"execute store {mode} score {t} {tail}"); return new ScoreOp(t); }
        var m = (IMethodSymbol)cur.Model.GetSymbolInfo(inv).Symbol!;
        string Sel0() => ClassOf(m.TypeArguments[0])!.BaseSelector;
        string Filter(int i) => args.Count > i ? ConstStr(args[i].Expression) : "";
        switch (name)
        {
            case "ent.addtag": Emit($"tag {Recv().Selector} add {ConstStr(args[0].Expression)}"); return null;
            case "ent.removetag": Emit($"tag {Recv().Selector} remove {ConstStr(args[0].Expression)}"); return null;
            case "ent.hastag": return Store("success", $"if entity {SelWith(Recv().Selector, "tag=" + ConstStr(args[0].Expression))}");
            case "ent.kill": Emit($"kill {Recv().Selector}"); return null;
            case "ent.merge": Emit($"data merge entity {Recv().Selector} {{{SnbtBody(ConstStr(args[0].Expression))}}}"); return null;
            case "ent.tp": { var r = Recv(); var pos = EvalPos(args[0].Expression); EmitCmd(At(pos, c => $"tp {r.Selector} {c}"), inv, pos); return null; }
            case "ent.run" or "player.run": { var r = Recv(); Emit($"execute as {r.Selector} at @s run {ActionCommand(args[0].Expression, true)}"); return null; }
            case "ents.count": return Store("result", $"if entity {SelWith(Sel0(), Filter(0))}".Replace("[]", ""));
            case "ents.any": return Store("success", $"if entity {SelWith(Sel0(), Filter(0))}".Replace("[]", ""));
            case "ents.spawn" or "ents.nearest" or "ents.random" or "ents.first": EvalRefExpr(inv); return null;
            case "check": return Store("success", $"if {ConstStr(args[0].Expression)}");
            case "result": return StoreCommand("result", args[0].Expression);
            case "success": return StoreCommand("success", args[0].Expression);
            case "random":
            {
                var t = Temp();
                cur.Sink.AddRange(MacroCmd($"execute store result score {t} run random value $(a)..$(b)", inv,
                    ("a", EvalNum(args[0].Expression, VK.Int), VK.Int), ("b", EvalNum(args[1].Expression, VK.Int), VK.Int)));
                return new ScoreOp(t);
            }
            case "schedule": Emit($"schedule function {FunctionOf(args[0].Expression)} {ConstSlot(args[1].Expression)}t"); return null;
            case "schedule.clear": Emit($"schedule clear {FunctionOf(args[0].Expression)}"); return null;
            case "exec.run": Emit($"execute {ConstStr(args[0].Expression)} run {ActionCommand(args[1].Expression, true)}"); return null;
            case "exec.at":
            {
                var pos = EvalPos(args[0].Expression);
                string ctx = PosContext(pos);
                string cmd = ActionCommand(args[1].Expression, false);
                EmitCmd(ctx.Length > 0 ? $"execute {ctx} run {cmd}" : cmd, inv, pos);
                return null;
            }
            case "asmf": EmitAsmf(args[0].Expression); return null;
        }
        return null;
    }

    string FunctionOf(ExpressionSyntax e)
    {
        var info = cur.Model.GetSymbolInfo(e);
        var m = (info.Symbol ?? info.CandidateSymbols.FirstOrDefault()) as IMethodSymbol;
        if (m == null || !fns.TryGetValue(m.OriginalDefinition, out var f) || f.Params.Count > 0)
            throw new UnsupportedException(e, "Expected a static MC# method without parameters.");
        return f.Path;
    }

    /// Command that runs a lambda or a method group (as the current @s).
    string ActionCommand(ExpressionSyntax arg, bool changesSelf)
    {
        if (arg is not LambdaExpressionSyntax lam) return CallbackCommand(arg);
        if (lam.DescendantNodes().OfType<ReturnStatementSyntax>().Any())
            throw new UnsupportedException(lam, "'return' is not supported inside lambdas; use if/else.");
        var cmds = Capture(() =>
        {
            if (changesSelf) cur.Depth++;
            try
            {
                var lps = lam is SimpleLambdaExpressionSyntax sl ? new List<ParameterSyntax> { sl.Parameter }
                    : ((ParenthesizedLambdaExpressionSyntax)lam).ParameterList.Parameters.ToList();
                foreach (var p in lps)
                    if (cur.Model.GetDeclaredSymbol(p) is IParameterSymbol ps)
                        cur.Locals[ps] = new RefVar("@s") { Depth = cur.Depth, Type = ps.Type };
                if (lam.Body is BlockSyntax b) EmitStatements(b.Statements); else EmitExprStatement((ExpressionSyntax)lam.Body);
            }
            finally { if (changesSelf) cur.Depth--; }
        });
        return cmds.Count == 1 ? cmds[0] : $"function {NewSub("fn", cmds)}";
    }

    // ================================================================ asm $"..." (macro commands)
    void EmitAsmf(ExpressionSyntax e)
    {
        var (cmd, args, force) = BuildTemplate(e);
        if (args.Length == 0 && !force) { Emit(cmd); return; }
        cur.Sink.AddRange(MacroCmd(cmd, e, args, force));
    }

    /// Emits "execute store <mode> score t run <command>" (command may contain runtime values) and returns t.
    Operand StoreCommand(string mode, ExpressionSyntax e)
    {
        var (cmd, args, force) = BuildTemplate(e);
        var t = Temp();
        Emit($"scoreboard players set {t} 0");
        string full = $"execute store {mode} score {t} run {cmd}";
        if (args.Length == 0 && !force) Emit(full); else cur.Sink.AddRange(MacroCmd(full, e, args, force));
        return new ScoreOp(t);
    }

    /// Command text from a constant or interpolated string; runtime holes become macro arguments.
    (string cmd, (string, Operand, VK)[] args, bool force) BuildTemplate(ExpressionSyntax e)
    {
        if (TryConstString(e) is string s) return (s.TrimStart('/'), [], false);
        if (e is not InterpolatedStringExpressionSyntax istr) throw new UnsupportedException(e, "A command string is required.");
        var sb = new System.Text.StringBuilder();
        var args = new List<(string, Operand, VK)>();
        bool force = false;
        int strArgs = 0;
        foreach (var c in istr.Contents)
        {
            if (c is InterpolatedStringTextSyntax t) { sb.Append(Unbrace(t.TextToken.ValueText)); continue; }
            var x = ((InterpolationSyntax)c).Expression;
            if (TryConstString(x) is string cs) { sb.Append(cs); continue; }
            var k = Kind(cur.Model.GetTypeInfo(x).Type);
            if (k == VK.Ref) { sb.Append(GetRef(x).Selector); continue; }
            if (k == VK.Entity) { sb.Append(Sel(GetEnt(x))); continue; }
            if (k == VK.NStr)
            {
                string name = $"s{strArgs++}";
                Emit($"data modify storage {Storage} arg.{name} set from storage {Storage} {StrPath(x)}");
                sb.Append($"$({name})"); force = true;
                continue;
            }
            if (k == VK.String && x is ElementAccessExpressionSyntax ea && ArrayOf(ea.Expression) is IFieldSymbol af)
            {
                string name = $"s{strArgs++}";
                CopyElement(af, EvalNum(ea.ArgumentList.Arguments[0].Expression, VK.Int), $"arg.{name}", ea);
                sb.Append($"$({name})"); force = true;
                continue;
            }
            if (k is VK.Int or VK.Fixed or VK.Double or VK.Bool)
            {
                var op = EvalNum(x, k);
                if (op is ConstOp co && !IsFrac(k)) { sb.Append(co.Value); continue; }
                string name = $"a{args.Count}";
                args.Add((name, op, k)); sb.Append($"$({name})");
                continue;
            }
            throw new UnsupportedException(x, "This value cannot be used in asm.");
        }
        return (sb.ToString().TrimStart('/'), args.ToArray(), force);
    }

    // ================================================================ constants, arrays, unrolling
    /// Compile-time string if e is fully constant (literals, consts, string arrays, [Inline] results, interpolation of constants).
    string? TryConstString(ExpressionSyntax e)
    {
        var cv = cur.Model.GetConstantValue(e);
        if (cv.HasValue) return cv.Value switch { null => null, bool b => b ? "true" : "false", IFormattable f => f.ToString(null, CultureInfo.InvariantCulture), var o => o.ToString() };
        switch (e)
        {
            case ParenthesizedExpressionSyntax p: return TryConstString(p.Expression);
            case ConditionalExpressionSyntax ce when Kind(cur.Model.GetTypeInfo(ce).Type) == VK.String:
            {
                Cond? c = null;
                var cmds = Capture(() => c = EvalCond(ce.Condition));
                return cmds.Count == 0 && c!.Const is bool b ? TryConstString(b ? ce.WhenTrue : ce.WhenFalse) : null;
            }
            case InterpolatedStringExpressionSyntax istr:
            {
                var sb = new System.Text.StringBuilder();
                foreach (var c in istr.Contents)
                {
                    if (c is InterpolatedStringTextSyntax t) sb.Append(Unbrace(t.TextToken.ValueText));
                    else if (TryConstString(((InterpolationSyntax)c).Expression) is string s) sb.Append(s);
                    else return null;
                }
                return sb.ToString();
            }
            case BinaryExpressionSyntax b when b.IsKind(SyntaxKind.AddExpression) && Kind(cur.Model.GetTypeInfo(b).Type) == VK.String:
                return TryConstString(b.Left) is string l && TryConstString(b.Right) is string r ? l + r : null;
            case ElementAccessExpressionSyntax ea when ArrayOf(ea.Expression) is IFieldSymbol af:
                return TryConstIndex(ea) is int i ? Element(af, i, ea)?.ToString() : null;
            case InvocationExpressionSyntax inv when cur.Model.GetSymbolInfo(inv).Symbol is IMethodSymbol m && HasAttr(m, "InlineAttribute") && m.ReturnType.SpecialType == SpecialType.System_String:
                EmitInline(m, inv, true, out var str);
                return str;
        }
        var sym = cur.Model.GetSymbolInfo(e).Symbol;
        if (sym != null && cur.Locals.TryGetValue(sym, out var v))
            return v switch { StrVar s => s.Value, ConstVar c => c.Text, _ => null };
        // compile-time number expression (e.g. {U.Atk[t] / 10}, {i * 0.25} in an unrolled loop)
        if (Kind(cur.Model.GetTypeInfo(e).Type) is VK.Int or VK.Fixed or VK.Double && Kind(cur.Model.GetTypeInfo(e).Type) is var ek)
        {
            Operand? op = null;
            List<string> cmds;
            try { cmds = Capture(() => op = EvalNum(e, ek)); } catch (UnsupportedException) { return null; }
            if (cmds.Count == 0 && op is ConstOp c) return FormatConst(c.Value, ek);
        }
        return null;
    }

    int? TryConstIndex(ElementAccessExpressionSyntax ea)
    {
        int? r = null;
        var cmds = Capture(() => { if (EvalNum(ea.ArgumentList.Arguments[0].Expression, VK.Int) is ConstOp c) r = c.Value; });
        return cmds.Count == 0 ? r : null;
    }

    IFieldSymbol? ArrayOf(ExpressionSyntax e) =>
        cur.Model.GetSymbolInfo(e).Symbol is IFieldSymbol f && arrays.ContainsKey(f) ? f : null;

    object? Element(IFieldSymbol f, int i, SyntaxNode at)
    {
        var list = arrays[f];
        if (i < 0 || i >= list.Count) throw new UnsupportedException(at, $"Index {i} is out of range for {f.Name} (length {list.Count}).");
        return list[i];
    }

    string ArrayPath(IFieldSymbol f) => $"arr.{Snake(f.ContainingType.Name)}_{Snake(f.Name)}";

    void DeclareArray(SemanticModel model, IFieldSymbol f, VariableDeclaratorSyntax v)
    {
        var init = v.Initializer?.Value;
        IEnumerable<ExpressionSyntax>? items = init switch
        {
            InitializerExpressionSyntax ie => ie.Expressions,
            ArrayCreationExpressionSyntax ac => ac.Initializer?.Expressions,
            ImplicitArrayCreationExpressionSyntax iac => iac.Initializer.Expressions,
            CollectionExpressionSyntax ce => ce.Elements.OfType<ExpressionElementSyntax>().Select(x => x.Expression),
            _ => null,
        };
        if (items == null) { Error(v, "Static readonly arrays need an initializer with constant elements."); return; }
        var list = new List<object?>();
        foreach (var it in items)
        {
            var cv = model.GetConstantValue(it);
            if (!cv.HasValue) { Error(it, "Array elements must be compile-time constants."); return; }
            list.Add(cv.Value);
        }
        arrays[f] = list;
    }

    /// Runtime read of an int array element (storage + macro).
    Operand ArrayElement(IFieldSymbol f, Operand idx, SyntaxNode at)
    {
        if (idx is ConstOp c) return new ConstOp(ScaleConst(Element(f, c.Value, at), Kind(((IArrayTypeSymbol)f.Type).ElementType)));
        runtimeArrays.Add(f);
        var t = Temp();
        var k = Kind(((IArrayTypeSymbol)f.Type).ElementType);
        cur.Sink.AddRange(MacroCmd($"execute store result score {t} run data get storage {Storage} {ArrayPath(f)}[$(i)] {ScaleOf(k)}", at, ("i", idx, VK.Int)));
        return new ScoreOp(t);
    }

    /// Copies a string array element (runtime index) to a storage path.
    void CopyElement(IFieldSymbol f, Operand idx, string path, SyntaxNode at)
    {
        runtimeArrays.Add(f);
        cur.Sink.AddRange(MacroCmd($"data modify storage {Storage} {path} set from storage {Storage} {ArrayPath(f)}[$(i)]", at, ("i", idx, VK.Int)));
    }

    List<Var>? UnrollValues(ExpressionSyntax src)
    {
        if (src is InvocationExpressionSyntax inv && Intrinsic(cur.Model.GetSymbolInfo(inv).Symbol) == "unroll.range")
        {
            int a = ConstSlot(inv.ArgumentList.Arguments[0].Expression), b = ConstSlot(inv.ArgumentList.Arguments[1].Expression);
            return Enumerable.Range(a, Math.Max(0, b - a)).Select(i => (Var)new ConstVar(i)).ToList();
        }
        if (ArrayOf(src) is IFieldSymbol f)
            return arrays[f].Select(o => o is string s ? new StrVar(s) : (Var)ConstOf(o, Kind(((IArrayTypeSymbol)f.Type).ElementType))).ToList();
        return null;
    }

    int ConstSlot2(ExpressionSyntax e) => EvalNum(e, VK.Int) is ConstOp c ? c.Value : throw new UnsupportedException(e, "A compile-time constant is required.");

    // ================================================================ foreach
    void EmitForEach(ForEachStatementSyntax fe)
    {
        if (ContainsWait(fe)) throw new UnsupportedException(fe, "Wait() cannot be used inside foreach.");
        var loopSym = cur.Model.GetDeclaredSymbol(fe)!;
        if (UnrollValues(fe.Expression) is List<Var> values)
        {
            if (fe.Statement.DescendantNodes(n => n is not (ForStatementSyntax or WhileStatementSyntax or DoStatementSyntax or ForEachStatementSyntax or SwitchStatementSyntax))
                .Any(n => n is BreakStatementSyntax or ContinueStatementSyntax))
                throw new UnsupportedException(fe, "break/continue are not supported in compile-time (unrolled) loops.");
            foreach (var v in values) { cur.Locals[loopSym] = v; EmitStmt(fe.Statement); }
            return;
        }
        if (ArrOf(fe.Expression) is ArrVar fa) { EmitArrayForEach(fe, fa, (ILocalSymbol)loopSym); return; }
        if (Kind(cur.Model.GetTypeInfo(fe.Expression).Type) == VK.NArr) { EmitNArrForEach(fe, GetNArr(fe.Expression), (ILocalSymbol)loopSym); return; }
        if (fe.Expression is not InvocationExpressionSyntax src || cur.Model.GetSymbolInfo(src).Symbol is not IMethodSymbol m || Intrinsic(m) != "ents.all")
            throw new UnsupportedException(fe.Expression, "foreach works over Entities.All<T>(...), Unroll.Range(...), an array, an NArray or a static readonly array.");
        var args = src.ArgumentList.Arguments;
        var t = m.TypeArguments[0];
        string sel = ClassOf(t)!.BaseSelector;
        if (args.Count > 0 && ConstStr(args[0].Expression) is { Length: > 0 } filter) sel = SelWith(sel, filter);
        bool at = args.Count > 1 && cur.Model.GetConstantValue(args[1].Expression).Value is true;
        bool needTag = fe.Statement.DescendantNodes().Any(n => n is ForEachStatementSyntax or LambdaExpressionSyntax);
        string tag = $"mcs.{ns}.e{refCounter++}";

        var loop = new LoopCtx { IsEntity = true };
        cur.Loops.Push(loop);
        cur.Depth++;
        cur.Locals[loopSym] = new RefVar("@s") { Depth = cur.Depth, Tag = needTag ? tag : null, Type = t };
        List<string> body;
        try
        {
            body = Capture(() =>
            {
                if (needTag) Emit($"tag @s add {tag}");
                EmitStmt(fe.Statement);
                if (loop.HasContinue) Emit($"execute if score {cur.Info.Ctl} matches 3 run scoreboard players set {cur.Info.Ctl} 0");
                if (needTag) Emit($"tag @s remove {tag}");
            });
        }
        finally { cur.Depth--; cur.Loops.Pop(); }
        if (loop.HasBreak) throw new UnsupportedException(fe, "break is not supported in entity loops (use continue or return).");
        if (fe.Statement.DescendantNodes().OfType<ReturnStatementSyntax>().Any())
            body.Insert(0, $"execute unless score {cur.Info.Ctl} matches 0 run return 0");
        string cmd = body.Count == 1 ? body[0] : $"function {NewSub("each", body)}";
        string head = $"execute as {sel} {(at ? "at @s " : "")}";
        Emit(cmd.StartsWith("execute ") ? head + cmd[8..] : head + "run " + cmd);
    }

    // ================================================================ switch
    void EmitSwitch(SwitchStatementSyntax sw)
    {
        if (ContainsWait(sw)) throw new UnsupportedException(sw, "Wait() cannot be used inside switch.");
        var v = EvalNum(sw.Expression, VK.Int);
        var sections = sw.Sections.Select(s => (s, values: s.Labels.OfType<CaseSwitchLabelSyntax>().Select(l => ConstSlot(l.Value)).ToList(),
            isDefault: s.Labels.Any(l => l is DefaultSwitchLabelSyntax), stmts: Body(s))).ToList();
        if (v is ConstOp c)
        {
            var pick = sections.FirstOrDefault(x => x.values.Contains(c.Value));
            if (pick.s == null) pick = sections.FirstOrDefault(x => x.isDefault);
            if (pick.s != null) EmitStatements(pick.stmts);
            return;
        }
        string t = ToTemp(v);
        var all = sections.SelectMany(x => x.values).ToList();
        var objectsBefore = cur.SaveObjects();
        var objectsAfter = new List<(Dictionary<Var, SyntaxNode>, Dictionary<Var, SyntaxNode>)>();
        foreach (var (s, values, isDefault, stmts) in sections)
        {
            cur.RestoreObjects(objectsBefore);
            var cmds = Capture(() => EmitStatements(stmts));
            objectsAfter.Add(cur.SaveObjects());
            if (cmds.Count == 0) continue;
            string cmd = cmds.Count == 1 ? cmds[0] : $"function {NewSub("case", cmds)}";
            if (isDefault) EmitGuarded(string.Join(" ", all.Where(x => !values.Contains(x)).Select(x => $"unless score {t} matches {x}")) is { Length: > 0 } g ? g : "if score " + t + " matches -2147483648..", new() { cmd }, "case");
            else foreach (var x in values) EmitGuarded($"if score {t} matches {x}", new() { cmd }, "case");
        }
        cur.RestoreObjects(objectsBefore);
        foreach (var o in objectsAfter) cur.MergeObjects(o);

        static List<StatementSyntax> Body(SwitchSectionSyntax s)
        {
            var list = s.Statements.Count == 1 && s.Statements[0] is BlockSyntax b ? b.Statements.ToList() : s.Statements.ToList();
            if (list.Count > 0 && list[^1] is BreakStatementSyntax) list.RemoveAt(list.Count - 1);
            if (list.SelectMany(x => x.DescendantNodesAndSelf(n => n is not (ForStatementSyntax or WhileStatementSyntax or DoStatementSyntax or ForEachStatementSyntax or SwitchStatementSyntax)))
                .Any(n => n is BreakStatementSyntax))
                throw new UnsupportedException(s, "'break' is only supported as the last statement of a switch section.");
            return list;
        }
    }

    // ================================================================ [Inline]
    Operand? EmitInline(IMethodSymbol m, InvocationExpressionSyntax inv, bool want, out string? strResult) => EmitInline(m, inv, want, out strResult, out _);

    Operand? EmitInline(IMethodSymbol m, InvocationExpressionSyntax inv, bool want, out string? strResult, out Var? objResult)
    {
        strResult = null; objResult = null;
        if (++inlineDepth > 32) throw new UnsupportedException(inv, "[Inline] recursion is too deep.");
        var decl = (MethodDeclarationSyntax)m.DeclaringSyntaxReferences[0].GetSyntax();
        var op = (IInvocationOperation)cur.Model.GetOperation(inv)!;
        var bound = new List<(ISymbol sym, Var? old)>();
        foreach (var a in op.Arguments)
        {
            var p = a.Parameter!;
            Var val;
            var k = Kind(p.Type);
            if (a.ArgumentKind == ArgumentKind.DefaultValue)
                val = k == VK.String ? new StrVar(p.ExplicitDefaultValue as string) : ConstOf(p.ExplicitDefaultValue, k);
            else
            {
                var x = (ExpressionSyntax)a.Value.Syntax;
                val = k switch
                {
                    VK.String => new StrVar(ConstString(x)),
                    VK.Int or VK.Bool or VK.Fixed or VK.Double => cur.Model.GetConstantValue(x) is { HasValue: true } xc ? ConstOf(xc.Value, k)
                        : EvalNum(x, k) is ConstOp c ? new ConstVar(c.Value, k) : new ScoreVar(ToTemp(EvalNum(x, k)), k),
                    VK.Position => new PosVar(EvalPos(x)),
                    VK.Ref => Resolve(GetRef(x)),
                    VK.Block or VK.Button or VK.Text or VK.Gui or VK.Item => EvalLibObject(x, k),
                    VK.Array => GetArr(x),
                    _ => throw new UnsupportedException(x, $"Unsupported [Inline] parameter type: {p.Type}"),
                };
            }
            bound.Add((p, cur.Locals.GetValueOrDefault(p)));
            cur.Locals[p] = val;
        }
        var savedModel = cur.Model;
        cur.Model = comp.GetSemanticModel(decl.SyntaxTree);
        Operand? result = null;
        try
        {
            ExpressionSyntax? ret = decl.ExpressionBody?.Expression;
            if (decl.Body != null)
            {
                var stmts = decl.Body.Statements.ToList();
                if (stmts.Count > 0 && stmts[^1] is ReturnStatementSyntax rs) { ret = rs.Expression; stmts.RemoveAt(stmts.Count - 1); }
                if (stmts.SelectMany(s => s.DescendantNodesAndSelf()).Any(n => n is ReturnStatementSyntax))
                    throw new UnsupportedException(decl, "[Inline] methods may only return at the end.");
                if (stmts.Any(ContainsWait)) throw new UnsupportedException(decl, "[Inline] methods cannot use Wait().");
                EmitStatements(stmts);
            }
            if (ret != null)
            {
                var rk = Kind(m.ReturnType);
                if (rk == VK.String) strResult = ConstString(ret);
                else if (rk is VK.Int or VK.Bool or VK.Fixed or VK.Double) result = EvalNum(ret, rk);
                else if (rk is VK.Button or VK.Text or VK.Block or VK.Ref or VK.Gui or VK.Item) objResult = EvalLibObject(ret, rk);
                else if (rk == VK.Void) EmitExprStatement(ret);
            }
        }
        finally
        {
            cur.Model = savedModel;
            foreach (var (sym, old) in bound) { if (old == null) cur.Locals.Remove(sym); else cur.Locals[sym] = old; }
            inlineDepth--;
        }
        return want ? result : null;
    }

    // ================================================================ load additions
    void FinishEntities(List<string> load)
    {
        foreach (var (name, (crit, disp, color)) in objectives)
            load.Insert(1, disp == null
                ? $"scoreboard objectives add {name} {crit}"
                : $"scoreboard objectives add {name} {crit} {{\"text\":{Json.Str(disp)}{(color != null ? $",\"color\":\"{color}\"" : "")}}}");
        foreach (var f in runtimeArrays)
            load.Add($"data modify storage {Storage} {ArrayPath(f)} set value [{string.Join(",", arrays[f].Select(o => o is string s ? Json.Str(s) : System.Convert.ToString(o, CultureInfo.InvariantCulture)))}]");
    }
}
