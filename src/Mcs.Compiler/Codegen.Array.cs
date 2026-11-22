using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mcs.Compiler;

/// A writable array (int[] / bool[] / float[] / double[]) with a compile-time length.
/// Element i is the score holder "<Base>[i]". Array variables are references: `b = a` makes b the same array.
sealed class ArrVar(string baseName, int length, VK elem) : Var
{
    public string Base = baseName;   // e.g. $static.grid, $main.a
    public int Length = length;
    public VK Elem = elem;
    public int TreeId = -1;          // runtime-index lookup functions, created on first use
}

/// Writable arrays. Constant indexes are plain score accesses; runtime indexes go through a per-array binary
/// dispatch tree (`execute if score $__arr.i matches …`), so no macros are needed (works on every version).
/// Out-of-range runtime reads give 0 and writes are ignored (Minecraft cannot throw).
public sealed partial class Codegen
{
    const int MaxArrayLength = 16384, ArrLeaf = 8;
    readonly Dictionary<IFieldSymbol, ArrVar> arrFields = new(SymbolEqualityComparer.Default);
    int arrTreeCounter;

    string ArrI => H("$__arr.i");
    string ArrV => H("$__arr.v");

    string ElemHolder(ArrVar a, int i, SyntaxNode at) => i >= 0 && i < a.Length
        ? H($"{a.Base}[{i}]")
        : throw new UnsupportedException(at, $"Index {i} is out of range (length {a.Length}).");

    static VK ElemKind(ITypeSymbol t) => Kind(((IArrayTypeSymbol)t).ElementType);

    /// The writable array an expression refers to (variable, static field or [Inline] parameter), or null.
    ArrVar? ArrOf(ExpressionSyntax e)
    {
        if (e is ParenthesizedExpressionSyntax p) return ArrOf(p.Expression);
        var sym = cur.Model.GetSymbolInfo(e).Symbol;
        if (sym == null) return null;
        if (cur.Locals.TryGetValue(sym, out var v)) return v as ArrVar;
        return sym is IFieldSymbol f && arrFields.TryGetValue(f, out var a) ? a : null;
    }

    ArrVar GetArr(ExpressionSyntax e) => ArrOf(e) ?? throw new UnsupportedException(e, "Expected an array variable.");

    // ---------------------------------------------------------------- creation
    /// Elements of an array initializer, or the size of `new T[n]`.
    static (ExpressionSyntax? size, IReadOnlyList<ExpressionSyntax>? items) ArrayInit(ExpressionSyntax init) => init switch
    {
        ArrayCreationExpressionSyntax { Initializer: { } ini } => (null, ini.Expressions),
        ArrayCreationExpressionSyntax ac when ac.Type.RankSpecifiers.Count == 1 && ac.Type.RankSpecifiers[0].Sizes.Count == 1
            && ac.Type.RankSpecifiers[0].Sizes[0] is not OmittedArraySizeExpressionSyntax => (ac.Type.RankSpecifiers[0].Sizes[0], null),
        ImplicitArrayCreationExpressionSyntax iac => (null, iac.Initializer.Expressions),
        InitializerExpressionSyntax ie => (null, ie.Expressions),
        CollectionExpressionSyntax ce when ce.Elements.All(x => x is ExpressionElementSyntax) => (null, ce.Elements.Cast<ExpressionElementSyntax>().Select(x => x.Expression).ToList()),
        _ => (null, null),
    };

    static int CheckLength(int n, SyntaxNode at) => n is >= 0 and <= MaxArrayLength ? n
        : throw new UnsupportedException(at, $"Array length must be 0..{MaxArrayLength} (got {n}).");

    void DeclareArrayField(SemanticModel model, IFieldSymbol f, VariableDeclaratorSyntax v)
    {
        var elem = ElemKind(f.Type);
        var init = v.Initializer?.Value;
        var (size, items) = init == null ? (null, null) : ArrayInit(init);
        string cls = f.ContainingType.Name == Preprocessor.GlobalClass ? "" : Snake(f.ContainingType.Name) + ".";
        try
        {
            if (size == null && items == null)
                throw new UnsupportedException(v, $"Static arrays need a size or elements: static {f.Type} {f.Name} = new {f.Type.ToDisplayString().TrimEnd('[', ']')}[10]; (or {{ 1, 2, 3 }}).");
            int n = size != null
                ? CheckLength(model.GetConstantValue(size).Value is int c ? c : throw new UnsupportedException(size, "The array length must be a compile-time constant."), size)
                : CheckLength(items!.Count, init!);
            var a = new ArrVar($"$static.{cls}{f.Name}", n, elem);
            arrFields[f] = a;
            // Like other static fields: initialized once, kept across reloads
            for (int i = 0; i < n; i++)
            {
                int val = 0;
                if (items != null)
                {
                    var cv = model.GetConstantValue(items[i]);
                    if (!cv.HasValue) throw new UnsupportedException(items[i], "Static array elements must be compile-time constants.");
                    val = ScaleConst(cv.Value, elem);
                }
                staticInits.Add((new ScoreVar(H($"{a.Base}[{i}]"), elem), val));
            }
        }
        catch (UnsupportedException e) { Error(e.Node, e.Message); }
    }

    /// `new T[n]` / `{ … }` → a new array (elements set now); any other array expression → the same array (reference).
    ArrVar ArrayValue(ExpressionSyntax init, string name, VK elem)
    {
        var (size, items) = ArrayInit(init);
        if (size == null && items == null) return GetArr(init);
        int n = size != null
            ? CheckLength(EvalNum(size, VK.Int) is ConstOp c ? c.Value : throw new UnsupportedException(size, "The array length must be a compile-time constant."), size)
            : CheckLength(items!.Count, init);
        string h = UniqueHolder(name);
        var a = new ArrVar(h[..h.LastIndexOf(' ')], n, elem);
        for (int i = 0; i < n; i++)
        {
            var ev = new ScoreVar(ElemHolder(a, i, init), elem);
            if (items == null) Emit($"scoreboard players set {ev.Holder} 0");
            else AssignHolder(ev, items[i]);
        }
        return a;
    }

    // ---------------------------------------------------------------- element access
    /// For a[i] on a writable array: the array and the index (a constant, or a temp holding it — evaluated once).
    (ArrVar arr, Operand idx)? ElemTarget(ExpressionSyntax e)
    {
        if (e is not ElementAccessExpressionSyntax ea || ArrOf(ea.Expression) is not ArrVar a) return null;
        if (ea.ArgumentList.Arguments.Count != 1) throw new UnsupportedException(ea, "Only one-dimensional arrays are supported.");
        var idx = EvalNum(ea.ArgumentList.Arguments[0].Expression, VK.Int);
        if (idx is ConstOp c) { ElemHolder(a, c.Value, ea); return (a, c); }
        return (a, new ScoreOp(ToTemp(idx)));
    }

    void EnsureTrees(ArrVar a)
    {
        if (a.TreeId >= 0) return;
        a.TreeId = arrTreeCounter++;
        string dir = $"{ns}:__arr/a{a.TreeId}";
        string Elem(int k) => H($"{a.Base}[{k}]");
        void Node(string op, int lo, int hi)
        {
            var cmds = new List<string>();
            if (hi - lo < ArrLeaf)
                for (int k = lo; k <= hi; k++)
                    cmds.Add(op == "get"
                        ? $"execute if score {ArrI} matches {k} run scoreboard players operation {ArrV} = {Elem(k)}"
                        : $"execute if score {ArrI} matches {k} run scoreboard players operation {Elem(k)} = {ArrV}");
            else
            {
                int mid = (lo + hi) / 2;
                cmds.Add($"execute if score {ArrI} matches {lo}..{mid} run function {dir}/{op}_{lo}_{mid}");
                cmds.Add($"execute if score {ArrI} matches {mid + 1}..{hi} run function {dir}/{op}_{mid + 1}_{hi}");
                Node(op, lo, mid); Node(op, mid + 1, hi);
            }
            pack.Functions[$"{dir}/{op}_{lo}_{hi}"] = cmds;
        }
        if (a.Length == 0) { pack.Functions[$"{dir}/get_0_-1"] = new(); pack.Functions[$"{dir}/set_0_-1"] = new(); return; }
        Node("get", 0, a.Length - 1);
        Node("set", 0, a.Length - 1);
    }

    /// Reads a[idx] into a fresh temp.
    Operand ArrGet(ArrVar a, Operand idx, SyntaxNode at)
    {
        if (idx is ConstOp c) return new ScoreOp(ElemHolder(a, c.Value, at));
        EnsureTrees(a);
        SetHolder(ArrI, idx);
        Emit($"scoreboard players set {ArrV} 0");   // out of range → 0
        Emit($"function {ns}:__arr/a{a.TreeId}/get_0_{a.Length - 1}");
        return new ScoreOp(ToTemp(new ScoreOp(ArrV)));
    }

    void ArrSet(ArrVar a, Operand idx, Operand value, SyntaxNode at)
    {
        if (idx is ConstOp c) { SetHolder(ElemHolder(a, c.Value, at), value); return; }
        EnsureTrees(a);
        SetHolder(ArrV, value);
        SetHolder(ArrI, idx);
        Emit($"function {ns}:__arr/a{a.TreeId}/set_0_{a.Length - 1}");
    }

    static char? CompoundOp(AssignmentExpressionSyntax a) => a.Kind() switch
    {
        SyntaxKind.SimpleAssignmentExpression => null,
        SyntaxKind.AddAssignmentExpression => '+', SyntaxKind.SubtractAssignmentExpression => '-',
        SyntaxKind.MultiplyAssignmentExpression => '*', SyntaxKind.DivideAssignmentExpression => '/',
        SyntaxKind.ModuloAssignmentExpression => '%',
        _ => throw new UnsupportedException(a, $"Unsupported assignment operator: {a.OperatorToken.Text}"),
    };

    /// a[i] = v, a[i] += v, …  Returns the stored value.
    ScoreVar EmitElemAssign(AssignmentExpressionSyntax asg, ArrVar a, Operand idx)
    {
        char? op = CompoundOp(asg);
        if (idx is ConstOp c)
        {
            var sv = new ScoreVar(ElemHolder(a, c.Value, asg), a.Elem);
            if (op == null) AssignHolder(sv, asg.Right); else ApplyOp(sv.Holder, op.Value, EvalNum(asg.Right, a.Elem), a.Elem);
            return sv;
        }
        ScoreVar t;
        if (op == null) { t = new ScoreVar(Temp(), a.Elem); AssignHolder(t, asg.Right); }
        else
        {
            t = new ScoreVar(((ScoreOp)ArrGet(a, idx, asg)).Holder, a.Elem);
            ApplyOp(t.Holder, op.Value, EvalNum(asg.Right, a.Elem), a.Elem);
        }
        ArrSet(a, idx, new ScoreOp(t.Holder), asg);
        return t;
    }

    /// ++/-- on an element. Returns the new value (or the old one when wantOld).
    Operand ElemIncDec(ArrVar a, Operand idx, bool inc, bool wantOld, SyntaxNode at)
    {
        string cmd(string h) => $"scoreboard players {(inc ? "add" : "remove")} {h} {ScaleOf(a.Elem)}";
        if (idx is ConstOp c)
        {
            string h = ElemHolder(a, c.Value, at);
            string? old = wantOld ? ToTemp(new ScoreOp(h)) : null;
            Emit(cmd(h));
            return new ScoreOp(old ?? h);
        }
        string cur0 = ((ScoreOp)ArrGet(a, idx, at)).Holder;
        string? prev = wantOld ? ToTemp(new ScoreOp(cur0)) : null;
        Emit(cmd(cur0));
        ArrSet(a, idx, new ScoreOp(cur0), at);
        return new ScoreOp(prev ?? cur0);
    }

    // ---------------------------------------------------------------- foreach / printing
    void EmitArrayForEach(ForEachStatementSyntax fe, ArrVar a, ILocalSymbol loopSym)
    {
        string i = UniqueHolder(loopSym.Name + "#i");
        var lv = new ScoreVar(UniqueHolder(loopSym.Name), a.Elem);
        cur.Locals[loopSym] = lv;
        Emit($"scoreboard players set {i} 0");
        EmitLoop(() => Cond.Of($"if score {i} matches ..{a.Length - 1}"),
            () => { SetHolder(lv.Holder, ArrGet(a, new ScoreOp(i), fe)); EmitStmt(fe.Statement); },
            MayAbort(fe.Statement),
            () => Emit($"scoreboard players add {i} 1"));
    }

    /// Text components for every element, separated by sep.
    List<string> ArrayComps(ArrVar a, string sep, SyntaxNode at)
    {
        var comps = new List<string>();
        for (int k = 0; k < a.Length; k++)
        {
            if (k > 0 && sep.Length > 0) comps.Add(Text(sep));
            string h = ElemHolder(a, k, at);
            comps.AddRange(a.Elem switch
            {
                VK.Bool => BoolComps(Cond.Of($"if score {h} matches 1")),
                VK.Fixed or VK.Double => FixedComps(new ScoreOp(h), a.Elem),
                _ => new() { ScoreComp(h) },
            });
        }
        return comps;
    }
}
