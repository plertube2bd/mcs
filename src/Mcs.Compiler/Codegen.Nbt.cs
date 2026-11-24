using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mcs.Compiler;

/// A runtime string: an NBT string at a path in <ns>:mcs ("v.main.s").
sealed class NStrVar(string path) : Var { public string Path = path; }

/// A runtime-length array: an NBT list at a path in <ns>:mcs. Elements: int (int, bool, Milli ×1000), double (Q16), string (NString).
sealed class NArrVar(string path, VK elem) : Var { public string Path = path; public VK Elem = elem; }

/// NString, NArray<T>, Storage (NbtLib) and Nbt (entity / block NBT).
public sealed partial class Codegen
{
    readonly Dictionary<IFieldSymbol, Var> nbtStatics = new(SymbolEqualityComparer.Default);   // static NString / NArray fields
    readonly List<string> nbtStaticInits = new();
    bool usesStrIndexOf;

    string DataStorage => $"{ns}:data";

    /// "$main.v.s" → "v.main.v.s" (holder names become nested compounds; '#' is not allowed in keys)
    static string StoragePathOf(string holderBase) => "v." + holderBase.TrimStart('$').Replace('#', '_');

    string TempPath() => StoragePathOf($"${cur.Info.Prefix}#s{cur.Temp++}");

    static string StorageComp(string path, string storage) => $"{{\"nbt\":{Json.Str(path)},\"storage\":{Json.Str(storage)}}}";
    string StorageComp(string path) => StorageComp(path, Storage);

    VK ElemKindOf(ITypeSymbol t, SyntaxNode? at = null)
    {
        var nt = (INamedTypeSymbol)SubstType(t);
        var k = Kind(nt.TypeArguments[0]);
        if (k is not (VK.Int or VK.Bool or VK.Fixed or VK.Double or VK.NStr))
            throw new UnsupportedException(at ?? cur?.Info.Decl ?? (SyntaxNode)SyntaxFactory.IdentifierName("NArray"), $"NArray elements must be int, bool, Milli, Q16 or NString (not {nt.TypeArguments[0].Name}).");
        return k;
    }

    // ---------------------------------------------------------------- element encoding
    static string ElemNbtType(VK k) => k == VK.Double ? "double" : "int";
    static string ElemStoreScale(VK k) => k == VK.Double ? InvScale(VK.Double) : "1";
    static int ElemGetScale(VK k) => k == VK.Double ? DScale : 1;
    static string ElemLiteral(int scaled, VK k) => k == VK.Double ? FmtScaled(scaled, k) + "d" : scaled.ToString(CultureInfo.InvariantCulture);

    /// Stores a number at a storage path with the type for kind k (bool → byte, Milli/Q16 → double).
    string StoreNumCmd(string storage, string path, Operand op, VK k) => op is ConstOp c
        ? $"data modify storage {storage} {path} set value {(k == VK.Bool ? (c.Value != 0 ? "1b" : "0b") : IsFrac(k) ? FmtScaled(c.Value, k) + "d" : c.Value.ToString(CultureInfo.InvariantCulture))}"
        : $"execute store result storage {storage} {path} {(k == VK.Bool ? "byte" : IsFrac(k) ? "double" : "int")} {InvScale(k)} run scoreboard players get {Holder(op)}";

    // ================================================================ NString
    abstract record SPart;
    sealed record SConst(string Text) : SPart;
    sealed record SPath(string Path) : SPart;
    sealed record SNum(Operand Op, VK Kind) : SPart;

    /// Writes the value of a string expression (constant, NString, concatenation, $"…") to dst.
    void StrInto(ExpressionSyntax e, string dst)
    {
        var parts = new List<SPart>();
        StrParts(e, parts);
        StrFromParts(parts, dst, e);
    }

    void StrFromParts(List<SPart> parts, string dst, SyntaxNode e)
    {
        // merge neighbouring constants
        var merged = new List<SPart>();
        foreach (var p in parts)
            if (p is SConst c && merged.Count > 0 && merged[^1] is SConst prev) merged[^1] = new SConst(prev.Text + c.Text);
            else merged.Add(p);
        if (merged.Count == 0) { Emit($"data modify storage {Storage} {dst} set value \"\""); return; }
        if (merged is [SConst only]) { Emit($"data modify storage {Storage} {dst} set value {Json.Str(only.Text)}"); return; }
        if (merged is [SPath one]) { if (one.Path != dst) Emit($"data modify storage {Storage} {dst} set from storage {Storage} {one.Path}"); return; }
        if (!prof.Macros)
            throw new UnsupportedException(e, "Joining strings at run time needs function macros (1.20.2+). Use '#if MC_1_20_2_OR_GREATER' or raise the minimum target version.");
        var sb = new System.Text.StringBuilder();
        int n = 0;
        foreach (var p in merged)
        {
            switch (p)
            {
                case SConst c: sb.Append(c.Text.Replace("\\", "\\\\").Replace("\"", "\\\"")); break;
                case SPath sp:
                    Emit($"data modify storage {Storage} arg.s{n} set from storage {Storage} {sp.Path}");
                    sb.Append($"$(s{n++})");
                    break;
                case SNum num:
                    Emit(num.Op is ConstOp cc
                        ? $"data modify storage {Storage} arg.s{n} set value {Json.Str(FmtScaled(cc.Value, num.Kind))}"
                        : $"execute store result storage {Storage} arg.s{n} {(num.Kind switch { VK.Fixed => "float", VK.Double => "double", _ => "int" })} {InvScale(num.Kind)} run scoreboard players get {Holder(num.Op)}");
                    sb.Append($"$(s{n++})");
                    break;
            }
        }
        Emit($"function {MacroFn($"data modify storage {Storage} {dst} set value \"{sb}\"")} with storage {Storage} arg");
    }

    void StrParts(ExpressionSyntax e, List<SPart> into)
    {
        e = Unparen(e);
        if (TryConstString(e) is string cs) { into.Add(new SConst(cs)); return; }
        var type = cur.Model.GetTypeInfo(e).Type;
        var k = Kind(type);
        switch (e)
        {
            case BinaryExpressionSyntax b when b.IsKind(SyntaxKind.AddExpression) && k is VK.NStr or VK.String:
                StrParts(b.Left, into); StrParts(b.Right, into); return;
            case InterpolatedStringExpressionSyntax istr:
                foreach (var c in istr.Contents)
                    if (c is InterpolatedStringTextSyntax t) into.Add(new SConst(Unbrace(t.TextToken.ValueText)));
                    else StrParts(((InterpolationSyntax)c).Expression, into);
                return;
        }
        switch (k)
        {
            case VK.NStr: into.Add(new SPath(StrPath(e))); return;
            case VK.String when e is ElementAccessExpressionSyntax ea && ArrayOf(ea.Expression) is IFieldSymbol af:
            {
                string p = TempPath();
                CopyElement(af, EvalNum(ea.ArgumentList.Arguments[0].Expression, VK.Int), p, ea);
                into.Add(new SPath(p));
                return;
            }
            case VK.Int or VK.Fixed or VK.Double:
            {
                var op = EvalNum(e, k);
                into.Add(op is ConstOp c ? new SConst(FmtScaled(c.Value, k)) : new SNum(new ScoreOp(ToTemp(op)), k));
                return;
            }
            case VK.Bool:
            {
                var c = EvalCond(e);
                if (c.Const is bool b) { into.Add(new SConst(b ? "True" : "False")); return; }
                string p = TempPath();
                Emit($"data modify storage {Storage} {p} set value \"False\"");
                Emit($"execute {string.Join(" ", c.Parts)} run data modify storage {Storage} {p} set value \"True\"");
                into.Add(new SPath(p));
                return;
            }
        }
        throw new UnsupportedException(e, k == VK.String ? "Strings must be compile-time constants (use NString for runtime strings)." : $"This value cannot be used in a string ({type}).");
    }

    /// The storage path holding a string value (a variable's own path, or a temporary).
    string StrPath(ExpressionSyntax e)
    {
        e = Unparen(e);
        if (e is IdentifierNameSyntax or MemberAccessExpressionSyntax && cur.Model.GetSymbolInfo(e).Symbol is not IPropertySymbol { IsStatic: true }
            && cur.Model.GetSymbolInfo(e).Symbol is not IMethodSymbol && TryGetVar(e) is NStrVar v)
            return v.Path;
        var sym = cur.Model.GetSymbolInfo(e).Symbol;
        string t = TempPath();
        switch (Intrinsic(sym))
        {
            case "nstr.sub" or "nstr.char":
            {
                var inv = e as InvocationExpressionSyntax;
                var recv = inv != null ? ((MemberAccessExpressionSyntax)inv.Expression).Expression : ((ElementAccessExpressionSyntax)e).Expression;
                var args = inv != null ? inv.ArgumentList.Arguments.Select(a => a.Expression).ToList() : ((ElementAccessExpressionSyntax)e).ArgumentList.Arguments.Select(a => a.Expression).ToList();
                string src = StrPath(recv);
                var start = EvalNum(args[0], VK.Int);
                if (args.Count == 1 && Intrinsic(sym) == "nstr.sub") { SubstringInto(src, t, start, null, e); return t; }
                var len = Intrinsic(sym) == "nstr.char" ? new ConstOp(1) : EvalNum(args[1], VK.Int);
                Operand end = start is ConstOp sc && len is ConstOp lc ? new ConstOp(sc.Value + lc.Value) : AddOps(start, len);
                SubstringInto(src, t, start, end, e);
                return t;
            }
            case "nstr.from":
                StrInto(((InvocationExpressionSyntax)e).ArgumentList.Arguments[0].Expression, t);
                return t;
            case "storage.get":
            {
                var inv = (InvocationExpressionSyntax)e;
                var (id, path) = StorageArgs(inv, 0, 1);
                Emit($"data modify storage {Storage} {t} set value \"\"");
                Emit($"data modify storage {Storage} {t} set from storage {id} {path}");
                return t;
            }
            case "nbt.get":
            {
                var inv = (InvocationExpressionSyntax)e;
                var (target, prefix) = NbtTarget(inv.ArgumentList.Arguments[0].Expression);
                Emit($"data modify storage {Storage} {t} set value \"\"");
                EmitPrefixed(prefix, $"data modify storage {Storage} {t} set from {target} {ConstStr(inv.ArgumentList.Arguments[1].Expression)}");
                return t;
            }
            case "narr.index":
            {
                var ea = (ElementAccessExpressionSyntax)e;
                var a = GetNArr(ea.Expression);
                Emit($"data modify storage {Storage} {t} set value \"\"");
                cur.Sink.AddRange(MacroCmd($"data modify storage {Storage} {t} set from storage {Storage} {a.Path}[$(i)]", ea, ("i", EvalNum(ea.ArgumentList.Arguments[0].Expression, VK.Int), VK.Int)));
                return t;
            }
            case "narr.pop":
            {
                var inv = (InvocationExpressionSyntax)e;
                var a = GetNArr(((MemberAccessExpressionSyntax)inv.Expression).Expression);
                Emit($"data modify storage {Storage} {t} set value \"\"");
                Emit($"data modify storage {Storage} {t} set from storage {Storage} {a.Path}[-1]");
                Emit($"data remove storage {Storage} {a.Path}[-1]");
                return t;
            }
        }
        if (e is InvocationExpressionSyntax call)
        {
            EmitInvocation(call, false);
            if (CallResult(call) is NStrVar r) { Emit($"data modify storage {Storage} {t} set from storage {Storage} {r.Path}"); return t; }
            throw new UnsupportedException(e, "This call does not return an NString.");
        }
        if (e is ConditionalExpressionSyntax ce)
        {
            string c = Materialize(EvalCond(ce.Condition));
            EmitGuarded($"if score {c} matches 1", Capture(() => StrInto(ce.WhenTrue, t)), "if");
            EmitGuarded($"if score {c} matches 0", Capture(() => StrInto(ce.WhenFalse, t)), "else");
            return t;
        }
        StrInto(e, t);
        return t;
    }

    Operand AddOps(Operand a, Operand b)
    {
        string t = ToTemp(a);
        ApplyOp(t, '+', b, VK.Int);
        return new ScoreOp(t);
    }

    /// dst = src[start..end] (end null: to the end). Out of range leaves dst unchanged (set it first).
    void SubstringInto(string src, string dst, Operand start, Operand? end, SyntaxNode at)
    {
        string tail = end == null ? "$(a)" : "$(a) $(b)";
        var args = new List<(string, Operand, VK)> { ("a", start, VK.Int) };
        if (end != null) args.Add(("b", end, VK.Int));
        cur.Sink.AddRange(MacroCmd($"data modify storage {Storage} {dst} set string storage {Storage} {src} {tail}", at, args.ToArray()));
    }

    /// a == b on strings: copy a to a scratch path, then "set from b" succeeds only if the value changes.
    Cond StrEquals(ExpressionSyntax l, ExpressionSyntax r, bool eq)
    {
        string scratch = StoragePathOf($"${cur.Info.Prefix}#cmp");
        string a = StrPathOrConst(l, out var la), b = StrPathOrConst(r, out var lb);
        Emit(la != null ? $"data modify storage {Storage} {scratch} set value {Json.Str(la)}" : $"data modify storage {Storage} {scratch} set from storage {Storage} {a}");
        string t = Temp();
        Emit(lb != null
            ? $"execute store success score {t} run data modify storage {Storage} {scratch} set value {Json.Str(lb)}"
            : $"execute store success score {t} run data modify storage {Storage} {scratch} set from storage {Storage} {b}");
        return Cond.Of($"if score {t} matches {(eq ? 0 : 1)}");
    }

    string StrPathOrConst(ExpressionSyntax e, out string? constant)
    {
        constant = TryConstString(Unparen(e));
        return constant != null ? "" : StrPath(e);
    }

    // ---------------------------------------------------------------- NString intrinsics
    Operand? EmitNStr(string name, InvocationExpressionSyntax inv)
    {
        var recv = inv.Expression is MemberAccessExpressionSyntax ma ? ma.Expression : null;
        var args = inv.ArgumentList.Arguments;
        switch (name)
        {
            case "nstr.starts" or "nstr.ends":
            {
                string src = StrPath(recv!);
                string w = StoragePathOf($"${cur.Info.Prefix}#w");
                Operand len = TryConstString(Unparen(args[0].Expression)) is string lit ? new ConstOp(lit.Length) : StrLength(StrPath(args[0].Expression));
                Emit($"data modify storage {Storage} {w} set value 0");   // stays a non-string if the text is shorter
                if (name == "nstr.starts") SubstringInto(src, w, new ConstOp(0), len, inv);
                else SubstringInto(src, w, len is ConstOp lc ? new ConstOp(-lc.Value) : Negated(len), null, inv);
                if (len is ConstOp { Value: 0 }) return new ConstOp(1);
                var c = StrEqualsPaths(w, args[0].Expression);
                string t = Temp(); StoreCond(t, c); return new ScoreOp(t);
            }
            case "nstr.indexof" or "nstr.contains":
            {
                if (!prof.Macros) throw new UnsupportedException(inv, "IndexOf / Contains need function macros (1.20.2+).");
                usesStrIndexOf = true;
                Emit($"data modify storage {Storage} str.h set from storage {Storage} {StrPath(recv!)}");
                if (TryConstString(Unparen(args[0].Expression)) is string lit) Emit($"data modify storage {Storage} str.n set value {Json.Str(lit)}");
                else Emit($"data modify storage {Storage} str.n set from storage {Storage} {StrPath(args[0].Expression)}");
                Emit($"function {ns}:__str/indexof");
                string t = Temp();
                if (name == "nstr.indexof") Emit($"scoreboard players operation {t} = {H("$__str.r")}");
                else Emit($"execute store success score {t} unless score {H("$__str.r")} matches -1");
                return new ScoreOp(t);
            }
            case "nstr.toint":
            {
                string t = Temp();
                Emit($"scoreboard players set {t} 0");
                Emit($"data modify storage {Storage} arg.s set from storage {Storage} {StrPath(recv!)}");
                if (!prof.Macros) throw new UnsupportedException(inv, "ToInt needs function macros (1.20.2+).");
                Emit($"function {MacroFn($"scoreboard players set {t} $(s)")} with storage {Storage} arg");
                return new ScoreOp(t);
            }
            case "nstr.sub" or "nstr.from":
                StrPath(inv);   // as a statement: evaluated for its effects only
                return null;
        }
        throw new UnsupportedException(inv, $"Cannot use '{name}' here.");
    }

    Cond StrEqualsPaths(string path, ExpressionSyntax other)
    {
        string scratch = StoragePathOf($"${cur.Info.Prefix}#cmp");
        Emit($"data modify storage {Storage} {scratch} set from storage {Storage} {path}");
        string t = Temp();
        Emit(TryConstString(Unparen(other)) is string lit
            ? $"execute store success score {t} run data modify storage {Storage} {scratch} set value {Json.Str(lit)}"
            : $"execute store success score {t} run data modify storage {Storage} {scratch} set from storage {Storage} {StrPath(other)}");
        return Cond.Of($"if score {t} matches 0");
    }

    Operand Negated(Operand op)
    {
        string t = ToTemp(op);
        Emit($"scoreboard players operation {t} *= {ConstHolder(-1)}");
        return new ScoreOp(t);
    }

    Operand StrLength(string path)
    {
        string t = Temp();
        Emit($"execute store result score {t} run data get storage {Storage} {path}");
        return new ScoreOp(t);
    }

    void FinishStrings()
    {
        if (!usesStrIndexOf) return;
        string S = Storage, d = $"{ns}:__str";
        string L = H("$__str.l"), N = H("$__str.n"), R = H("$__str.r"), I = H("$__str.i"), E = H("$__str.e"), D = H("$__str.d");
        pack.Functions[$"{d}/indexof"] = new()
        {
            $"execute store result score {L} run data get storage {S} str.h",
            $"execute store result score {N} run data get storage {S} str.n",
            $"scoreboard players set {R} -1",
            $"scoreboard players set {I} 0",
            $"scoreboard players operation {E} = {N}",
            $"execute if score {E} <= {L} run function {d}/step",
        };
        pack.Functions[$"{d}/step"] = new()
        {
            $"execute store result storage {S} str.i int 1 run scoreboard players get {I}",
            $"execute store result storage {S} str.e int 1 run scoreboard players get {E}",
            $"function {d}/cmp with storage {S} str",
            $"scoreboard players add {I} 1",
            $"scoreboard players add {E} 1",
            $"execute if score {R} matches -1 if score {E} <= {L} run function {d}/step",
        };
        pack.Functions[$"{d}/cmp"] = new()
        {
            $"$data modify storage {S} str.w set string storage {S} str.h $(i) $(e)",
            $"execute store success score {D} run data modify storage {S} str.w set from storage {S} str.n",
            $"execute if score {D} matches 0 run scoreboard players operation {R} = {I}",
        };
    }

    // ================================================================ NArray
    NArrVar GetNArr(ExpressionSyntax e) => Unparen(e) is InvocationExpressionSyntax inv ? NArrResult(inv)
        : TryGetVar(Unparen(e)) as NArrVar ?? throw new UnsupportedException(e, "Expected an NArray variable.");

    NArrVar NArrResult(InvocationExpressionSyntax inv)
    {
        EmitInvocation(inv, false);
        return CallResult(inv) as NArrVar ?? throw new UnsupportedException(inv, "This call does not return an NArray.");
    }

    Var? TryGetVar(ExpressionSyntax e)
    {
        try { return GetVar(e); } catch (UnsupportedException) { return null; }
    }

    NArrVar NewArrValue(ISymbol sym, ExpressionSyntax init)
    {
        var e = Unparen(init);
        var elem = ElemKindOf(cur.Model.GetTypeInfo(e).Type ?? cur.Model.GetTypeInfo(e).ConvertedType!, e);
        if (e is BaseObjectCreationExpressionSyntax or InvocationExpressionSyntax)
        {
            var a = new NArrVar(StoragePathOf(BaseOf(UniqueHolder(sym.Name))), elem);
            ArrInto(e, a);
            return a;
        }
        return GetNArr(e);
    }

    /// dst = value of an NArray expression (a new array, a copy of another one, a call result).
    void ArrInto(ExpressionSyntax e, NArrVar dst)
    {
        e = Unparen(e);
        if (e is BaseObjectCreationExpressionSyntax oc)
        {
            var args = oc.ArgumentList?.Arguments ?? default;
            string zero = dst.Elem == VK.NStr ? "\"\"" : ElemLiteral(0, dst.Elem);
            if (args.Count == 0) Emit($"data modify storage {Storage} {dst.Path} set value []");
            else if (EvalNum(args[0].Expression, VK.Int) is ConstOp { Value: >= 0 and <= 256 } n)
                Emit($"data modify storage {Storage} {dst.Path} set value [{string.Join(",", Enumerable.Repeat(zero, n.Value))}]");
            else
            {
                string cnt = ToTemp(EvalNum(args[0].Expression, VK.Int)), loop = ReservePath("fill");
                pack.Functions[loop] = new() { $"data modify storage {Storage} {dst.Path} append value {zero}", $"scoreboard players remove {cnt} 1", $"execute if score {cnt} matches 1.. run function {loop}" };
                Emit($"data modify storage {Storage} {dst.Path} set value []");
                Emit($"execute if score {cnt} matches 1.. run function {loop}");
            }
            if (oc.Initializer != null)
                foreach (var x in oc.Initializer.Expressions) NArrAdd(dst, x, null);
            return;
        }
        if (e is InvocationExpressionSyntax inv)
        {
            EmitInvocation(inv, false);
            if (CallResult(inv) is NArrVar r) { CopyVar(r, dst); return; }
            throw new UnsupportedException(e, "This call does not return an NArray.");
        }
        CopyVar(GetNArr(e), dst);
    }

    void NArrAdd(NArrVar a, ExpressionSyntax value, Operand? index)
    {
        string slot = index == null ? "-1" : "$(i)";
        string ins = index == null ? "append" : "insert $(i)";
        var idxArgs = index == null ? Array.Empty<(string, Operand, VK)>() : new[] { ("i", index, VK.Int) };
        if (a.Elem == VK.NStr)
        {
            string src = TryConstString(Unparen(value)) is string lit ? "" : StrPath(value);
            string cmd = src.Length == 0
                ? $"data modify storage {Storage} {a.Path} {ins} value {Json.Str(TryConstString(Unparen(value))!)}"
                : $"data modify storage {Storage} {a.Path} {ins} from storage {Storage} {src}";
            cur.Sink.AddRange(MacroCmd(cmd, value, idxArgs));
            return;
        }
        var v = EvalNum(value, a.Elem);
        if (v is ConstOp c) { cur.Sink.AddRange(MacroCmd($"data modify storage {Storage} {a.Path} {ins} value {ElemLiteral(c.Value, a.Elem)}", value, idxArgs)); return; }
        string h = ToTemp(v);
        cur.Sink.AddRange(MacroCmd($"data modify storage {Storage} {a.Path} {ins} value {ElemLiteral(0, a.Elem)}", value, idxArgs));
        cur.Sink.AddRange(MacroCmd($"execute store result storage {Storage} {a.Path}[{slot}] {ElemNbtType(a.Elem)} {ElemStoreScale(a.Elem)} run scoreboard players get {h}", value, idxArgs));
    }

    /// a[i] (numbers): out of range reads 0.
    Operand NArrGet(NArrVar a, Operand idx, SyntaxNode at)
    {
        if (a.Elem == VK.NStr) throw new UnsupportedException(at, "NArray<NString> elements are strings, not numbers.");
        string t = Temp();
        Emit($"scoreboard players set {t} 0");
        cur.Sink.AddRange(MacroCmd($"execute store result score {t} run data get storage {Storage} {a.Path}[$(i)] {ElemGetScale(a.Elem)}", at, ("i", idx, VK.Int)));
        return new ScoreOp(t);
    }

    void NArrSet(NArrVar a, Operand idx, ExpressionSyntax value, SyntaxNode at)
    {
        if (a.Elem == VK.NStr)
        {
            string cmd = TryConstString(Unparen(value)) is string lit
                ? $"data modify storage {Storage} {a.Path}[$(i)] set value {Json.Str(lit)}"
                : $"data modify storage {Storage} {a.Path}[$(i)] set from storage {Storage} {StrPath(value)}";
            cur.Sink.AddRange(MacroCmd(cmd, at, ("i", idx, VK.Int)));
            return;
        }
        NArrSetNum(a, idx, EvalNum(value, a.Elem), at);
    }

    void NArrSetNum(NArrVar a, Operand idx, Operand v, SyntaxNode at)
    {
        if (v is ConstOp c) cur.Sink.AddRange(MacroCmd($"data modify storage {Storage} {a.Path}[$(i)] set value {ElemLiteral(c.Value, a.Elem)}", at, ("i", idx, VK.Int)));
        else cur.Sink.AddRange(MacroCmd($"execute store result storage {Storage} {a.Path}[$(i)] {ElemNbtType(a.Elem)} {ElemStoreScale(a.Elem)} run scoreboard players get {Holder(v)}", at, ("i", idx, VK.Int)));
    }

    Operand? EmitNArr(string name, InvocationExpressionSyntax inv)
    {
        var a = GetNArr(((MemberAccessExpressionSyntax)inv.Expression).Expression);
        var args = inv.ArgumentList.Arguments;
        switch (name)
        {
            case "narr.add": NArrAdd(a, args[0].Expression, null); return null;
            case "narr.insert": NArrAdd(a, args[1].Expression, EvalNum(args[0].Expression, VK.Int)); return null;
            case "narr.removeat": cur.Sink.AddRange(MacroCmd($"data remove storage {Storage} {a.Path}[$(i)]", inv, ("i", EvalNum(args[0].Expression, VK.Int), VK.Int))); return null;
            case "narr.clear": Emit($"data modify storage {Storage} {a.Path} set value []"); return null;
            case "narr.pop":
            {
                if (a.Elem == VK.NStr) { StrPath(inv); return null; }
                string t = Temp();
                Emit($"scoreboard players set {t} 0");
                Emit($"execute store result score {t} run data get storage {Storage} {a.Path}[-1] {ElemGetScale(a.Elem)}");
                Emit($"data remove storage {Storage} {a.Path}[-1]");
                return new ScoreOp(t);
            }
        }
        throw new UnsupportedException(inv, $"Cannot use '{name}' here.");
    }

    /// foreach over an NArray: walks a copy, taking the first element each time (no macros needed).
    void EmitNArrForEach(ForEachStatementSyntax fe, NArrVar a, ILocalSymbol loopSym)
    {
        string it = StoragePathOf($"${cur.Info.Prefix}#it{cur.Temp++}");
        Var lv = a.Elem == VK.NStr ? new NStrVar(StoragePathOf(BaseOf(UniqueHolder(loopSym.Name)))) : new ScoreVar(UniqueHolder(loopSym.Name), a.Elem);
        cur.Locals[loopSym] = lv;
        Emit($"data modify storage {Storage} {it} set from storage {Storage} {a.Path}");
        EmitLoop(() => Cond.Of($"if data storage {Storage} {it}[0]"),
            () =>
            {
                if (lv is NStrVar sv) Emit($"data modify storage {Storage} {sv.Path} set from storage {Storage} {it}[0]");
                else Emit($"execute store result score {((ScoreVar)lv).Holder} run data get storage {Storage} {it}[0] {ElemGetScale(a.Elem)}");
                Emit($"data remove storage {Storage} {it}[0]");
                EmitStmt(fe.Statement);
            },
            MayAbort(fe.Statement), null);
    }

    List<string> NArrComps(NArrVar a) => [Text("["), $"{{\"nbt\":{Json.Str(a.Path + "[]")},\"storage\":{Json.Str(Storage)},\"separator\":\", \"}}", Text("]")];

    // ---------------------------------------------------------------- indexers (NString, NArray, Objective)
    string? IndexerIntrinsic(ElementAccessExpressionSyntax ea) =>
        cur.Model.GetSymbolInfo(ea).Symbol is IPropertySymbol { IsIndexer: true } ip ? Intrinsic(ip) : null;

    Operand IndexGet(string name, ElementAccessExpressionSyntax ea, VK kind)
    {
        var idx = ea.ArgumentList.Arguments[0].Expression;
        switch (name)
        {
            case "narr.index":
            {
                var a = GetNArr(ea.Expression);
                return Convert(NArrGet(a, EvalNum(idx, VK.Int), ea), a.Elem, kind, ea);
            }
            case "score.index": return new ScoreOp(((ScoreVar)GetVar(ea)).Holder);
        }
        throw new UnsupportedException(ea, "This element cannot be used as a number.");
    }

    /// a[i] = v / a[i] op= v on an NArray.
    bool EmitIndexAssign(AssignmentExpressionSyntax asg)
    {
        if (asg.Left is not ElementAccessExpressionSyntax ea || IndexerIntrinsic(ea) != "narr.index") return false;
        var a = GetNArr(ea.Expression);
        var idx = EvalNum(ea.ArgumentList.Arguments[0].Expression, VK.Int);
        if (idx is ScoreOp) idx = new ScoreOp(ToTemp(idx));
        var op = CompoundOp(asg);
        if (op == null) { NArrSet(a, idx, asg.Right, asg); return true; }
        if (a.Elem == VK.NStr)
        {
            if (op != '+') throw new UnsupportedException(asg, "Only += works on strings.");
            throw new UnsupportedException(asg, "Use a[i] = a[i] + value for NArray<NString>.");
        }
        string t = ((ScoreOp)NArrGet(a, idx, asg)).Holder;
        ApplyOp(t, op.Value, EvalNum(asg.Right, a.Elem), a.Elem);
        NArrSetNum(a, idx, new ScoreOp(t), asg);
        return true;
    }

    ScoreVar? IndexIncDec(ExpressionSyntax target, bool inc)
    {
        if (target is not ElementAccessExpressionSyntax ea || IndexerIntrinsic(ea) != "narr.index") return null;
        var a = GetNArr(ea.Expression);
        var idx = EvalNum(ea.ArgumentList.Arguments[0].Expression, VK.Int);
        if (idx is ScoreOp) idx = new ScoreOp(ToTemp(idx));
        string t = ((ScoreOp)NArrGet(a, idx, ea)).Holder;
        Emit($"scoreboard players {(inc ? "add" : "remove")} {t} {ScaleOf(a.Elem)}");
        NArrSetNum(a, idx, new ScoreOp(t), ea);
        return new ScoreVar(t, a.Elem);
    }

    // ================================================================ Storage (NbtLib)
    /// Storage id (optional argument, default <ns>:data) and the path argument.
    (string id, string path) StorageArgs(InvocationExpressionSyntax inv, int pathArg, int idArg)
    {
        var op = (Microsoft.CodeAnalysis.Operations.IInvocationOperation)cur.Model.GetOperation(inv)!;
        string? id = null;
        foreach (var a in op.Arguments)
            if (a.Parameter!.Name == "storage" && a.ArgumentKind != Microsoft.CodeAnalysis.Operations.ArgumentKind.DefaultValue)
                id = ConstString((ExpressionSyntax)a.Value.Syntax);
        id ??= DataStorage;
        if (!id.Contains(':')) id = "minecraft:" + id;
        return (id, ConstStr(inv.ArgumentList.Arguments[pathArg].Expression));
    }

    ITypeSymbol TypeArg(InvocationExpressionSyntax inv) => SubstType(((IMethodSymbol)cur.Model.GetSymbolInfo(inv).Symbol!).TypeArguments[0]);

    /// NBT source/target of an entity (an Entity or entity reference) or a block (a Position): ("entity <sel>" | "block <x y z>", execute prefix)
    (string target, string prefix) NbtTarget(ExpressionSyntax e)
    {
        var k = Kind(cur.Model.GetTypeInfo(e).Type);
        if (k == VK.Position)
        {
            var p = EvalPos(e);
            if (p.Args.Length > 0) throw new UnsupportedException(e, "Block NBT needs a position with constant coordinates.");
            return ($"block {p.Coords}", p.Prefix);
        }
        return ($"entity {SingleSel(e)}", "");
    }

    void EmitPrefixed(string prefix, string cmd) => Emit(prefix.Length > 0 ? $"execute {prefix} run {cmd}" : cmd);

    Operand? EmitStorage(string name, InvocationExpressionSyntax inv)
    {
        var args = inv.ArgumentList.Arguments;
        switch (name)
        {
            case "storage.get" or "nbt.get":
            {
                var t = TypeArg(inv);
                var k = Kind(t);
                if (k == VK.NStr) { StrPath(inv); return null; }
                if (k is not (VK.Int or VK.Bool or VK.Fixed or VK.Double)) throw new UnsupportedException(inv, $"Get<{t}> is not supported (int, bool, Milli, Q16, NString).");
                string src, prefix = "";
                if (name == "storage.get") { var (id, path) = StorageArgs(inv, 0, 1); src = $"storage {id} {path}"; }
                else { var (tg, pf) = NbtTarget(args[0].Expression); src = $"{tg} {ConstStr(args[1].Expression)}"; prefix = pf; }
                string h = Temp();
                Emit($"scoreboard players set {h} 0");
                EmitPrefixed(prefix, $"execute store result score {h} run data get {src} {ScaleOf(k)}");
                return new ScoreOp(h);
            }
            case "storage.set" or "nbt.set":
            {
                string dstTarget, path, prefix = "";
                ExpressionSyntax value;
                if (name == "storage.set") { var (id, p) = StorageArgs(inv, 0, 2); dstTarget = $"storage {id}"; path = p; value = args[1].Expression; }
                else { var (tg, pf) = NbtTarget(args[0].Expression); dstTarget = tg; prefix = pf; path = ConstStr(args[1].Expression); value = args[2].Expression; }
                var t = TypeArg(inv);
                var k = Kind(t);
                if (k is VK.String or VK.NStr)
                {
                    if (TryConstString(Unparen(value)) is string lit) EmitPrefixed(prefix, $"data modify {dstTarget} {path} set value {Json.Str(lit)}");
                    else EmitPrefixed(prefix, $"data modify {dstTarget} {path} set from storage {Storage} {StrPath(value)}");
                    return null;
                }
                if (k == VK.NArr) { EmitPrefixed(prefix, $"data modify {dstTarget} {path} set from storage {Storage} {GetNArr(value).Path}"); return null; }
                if (k is not (VK.Int or VK.Bool or VK.Fixed or VK.Double)) throw new UnsupportedException(inv, $"Set<{t}> is not supported.");
                var op = k == VK.Bool ? (EvalCond(value) is { Const: bool b } ? new ConstOp(b ? 1 : 0) : new ScoreOp(Materialize(EvalCond(value)))) : EvalNum(value, k);
                string typ = k == VK.Bool ? "byte" : IsFrac(k) ? "double" : "int";
                if (op is ConstOp c)
                    EmitPrefixed(prefix, $"data modify {dstTarget} {path} set value {(k == VK.Bool ? c.Value + "b" : IsFrac(k) ? FmtScaled(c.Value, k) + "d" : c.Value.ToString(CultureInfo.InvariantCulture))}");
                else Emit($"execute {(prefix.Length > 0 ? prefix + " " : "")}store result {dstTarget} {path} {typ} {InvScale(k)} run scoreboard players get {Holder(op)}");
                return null;
            }
            case "storage.setnbt":
            {
                var (id, path) = StorageArgs(inv, 0, 2);
                Emit($"data modify storage {id} {path} set value {ConstStr(args[1].Expression)}");
                return null;
            }
            case "storage.has":
            {
                var (id, path) = StorageArgs(inv, 0, 1);
                string t = Temp();
                Emit($"execute store success score {t} if data storage {id} {path}");
                return new ScoreOp(t);
            }
            case "nbt.has":
            {
                var (tg, pf) = NbtTarget(args[0].Expression);
                string t = Temp();
                Emit($"execute store success score {t} {(pf.Length > 0 ? pf + " " : "")}if data {tg} {ConstStr(args[1].Expression)}");
                return new ScoreOp(t);
            }
            case "storage.remove": { var (id, path) = StorageArgs(inv, 0, 1); Emit($"data remove storage {id} {path}"); return null; }
            case "nbt.remove": { var (tg, pf) = NbtTarget(args[0].Expression); EmitPrefixed(pf, $"data remove {tg} {ConstStr(args[1].Expression)}"); return null; }
            case "storage.copy":
            {
                var (id, from) = StorageArgs(inv, 0, 2);
                Emit($"data modify storage {id} {ConstStr(args[1].Expression)} set from storage {id} {from}");
                return null;
            }
            case "storage.count":
            {
                var (id, path) = StorageArgs(inv, 0, 1);
                string t = Temp();
                Emit($"scoreboard players set {t} 0");
                Emit($"execute store result score {t} run data get storage {id} {path}");
                return new ScoreOp(t);
            }
            case "storage.merge":
            {
                var (id, _) = StorageArgs(inv, 0, 1);
                Emit($"data merge storage {id} {ConstStr(args[0].Expression)}");
                return null;
            }
            case "nbt.merge": { var (tg, pf) = NbtTarget(args[0].Expression); EmitPrefixed(pf, $"data merge {tg} {ConstStr(args[1].Expression)}"); return null; }
            case "nbt.save":
            {
                var (tg, pf) = NbtTarget(args[0].Expression);
                var (id, spath) = StorageArgs(inv, 2, 3);
                EmitPrefixed(pf, $"data modify storage {id} {spath} set from {tg} {ConstStr(args[1].Expression)}");
                return null;
            }
        }
        throw new UnsupportedException(inv, $"Cannot use '{name}' here.");
    }

    // ---------------------------------------------------------------- static NString / NArray fields
    void DeclareNbtStatic(SemanticModel model, IFieldSymbol f, VariableDeclaratorSyntax v, VK k)
    {
        string cls = f.ContainingType.Name == Preprocessor.GlobalClass ? "" : Snake(f.ContainingType.Name) + ".";
        string path = StoragePathOf($"$static.{cls}{f.Name}");
        var init = v.Initializer?.Value;
        if (k == VK.NStr)
        {
            nbtStatics[f] = new NStrVar(path);
            string? s = init == null ? "" : model.GetConstantValue(init).Value as string;
            if (s == null) { Error(init!, "Static NString initializers must be compile-time constants."); return; }
            nbtStaticInits.Add($"execute unless data storage {Storage} {path} run data modify storage {Storage} {path} set value {Json.Str(s)}");
            return;
        }
        VK elem;
        try { elem = ElemKindOf(f.Type); } catch (UnsupportedException e) { Error(v, e.Message); return; }
        nbtStatics[f] = new NArrVar(path, elem);
        var items = new List<string>();
        if (init is BaseObjectCreationExpressionSyntax oc)
        {
            var a = oc.ArgumentList?.Arguments ?? default;
            if (a.Count > 0)
            {
                if (model.GetConstantValue(a[0].Expression).Value is not int n || n < 0 || n > 1024) { Error(a[0], "The length of a static NArray must be a constant (0–1024)."); return; }
                items.AddRange(Enumerable.Repeat(elem == VK.NStr ? "\"\"" : ElemLiteral(0, elem), n));
            }
            foreach (var x in oc.Initializer?.Expressions ?? default)
            {
                var cv = model.GetConstantValue(x);
                if (!cv.HasValue) { Error(x, "Static NArray elements must be compile-time constants."); return; }
                items.Add(elem == VK.NStr ? Json.Str(cv.Value as string ?? "") : ElemLiteral(ScaleConst(cv.Value, elem), elem));
            }
        }
        else if (init != null) { Error(init, "Static NArray fields need new NArray<T>() / new NArray<T> { … }."); return; }
        nbtStaticInits.Add($"execute unless data storage {Storage} {path} run data modify storage {Storage} {path} set value [{string.Join(",", items)}]");
    }
}
