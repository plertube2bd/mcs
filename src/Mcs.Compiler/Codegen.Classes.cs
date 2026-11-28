using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mcs.Compiler;

/// An object of a plain class: its fields are flattened into scores (and storage paths for NString / NArray),
/// so field access is a plain scoreboard operation. Base = holder prefix ("$main.v"); fields are "$main.v.x".
/// Object variables are references fixed at compile time (`b = a` makes b the same object); objects stored in
/// fields of other objects are embedded (assigning one copies its fields).
sealed class ObjVar(INamedTypeSymbol type, string @base) : Var
{
    public INamedTypeSymbol Type = type;
    public string Base = @base;
    public Dictionary<string, Var> Fields = new();
}

/// Plain classes and generics.
///   - Instance methods and constructors compile to one function per method, with a `this` frame of scores
///     ($vec.len#this.x): a call copies the receiver into the frame, runs the function and copies it back
///     (only if the method can change the object).
///   - Virtual methods are resolved at compile time from the object's actual class.
///   - Generic methods and generic classes are compiled once per set of type arguments (Max&lt;int&gt;, Box&lt;Milli&gt;).
public sealed partial class Codegen
{
    [ThreadStatic] static Dictionary<ITypeParameterSymbol, ITypeSymbol>? subst;

    readonly Dictionary<IMethodSymbol, FnInfo> instances = new(SymbolEqualityComparer.Default);
    readonly Queue<FnInfo> pendingFns = new();
    readonly Dictionary<IFieldSymbol, ObjVar> objStatics = new(SymbolEqualityComparer.Default);
    readonly List<(IFieldSymbol f, ObjVar obj, ExpressionSyntax? init)> staticObjInits = new();
    readonly Dictionary<FnInfo, bool> mutatesThis = new();
    int objTemp;

    // ================================================================ types
    static bool IsUserClass(ITypeSymbol? t) =>
        t is INamedTypeSymbol { TypeKind: TypeKind.Class or TypeKind.Struct, IsStatic: false } n
        && n.Name != Preprocessor.GlobalClass && !IsDummyClass(n) && ClassOf(n) == null
        && n.DeclaringSyntaxReferences.Length > 0 && n.DeclaringSyntaxReferences.All(r => !r.SyntaxTree.FilePath.StartsWith('<'));

    static bool IsNArrType(ITypeSymbol? t) => t is INamedTypeSymbol { Name: "NArray" } n && n.ContainingNamespace?.Name == "MCS";

    static bool IsTemplate(IMethodSymbol def)
    {
        if (def.IsGenericMethod) return true;
        for (var t = def.ContainingType; t != null; t = t.ContainingType) if (t.IsGenericType) return true;
        return false;
    }

    ITypeSymbol SubstType(ITypeSymbol t)
    {
        switch (t)
        {
            case ITypeParameterSymbol tp: return subst != null && subst.TryGetValue(tp, out var c) ? c : t;
            case IArrayTypeSymbol at: { var e = SubstType(at.ElementType); return SymbolEqualityComparer.Default.Equals(e, at.ElementType) ? t : comp.CreateArrayTypeSymbol(e, at.Rank); }
            case INamedTypeSymbol { IsGenericType: true } nt:
            {
                var args = nt.TypeArguments.Select(SubstType).ToArray();
                return args.Zip(nt.TypeArguments).All(p => SymbolEqualityComparer.Default.Equals(p.First, p.Second)) ? t : nt.OriginalDefinition.Construct(args);
            }
        }
        return t;
    }

    /// The method a call really runs, with the current type arguments filled in.
    IMethodSymbol ConcreteMethod(IMethodSymbol m)
    {
        var ct = m.ContainingType;
        var sct = (INamedTypeSymbol)SubstType(ct);
        var baseM = SymbolEqualityComparer.Default.Equals(sct, ct) ? m.ConstructedFrom
            : sct.GetMembers(m.Name).OfType<IMethodSymbol>().First(x => SymbolEqualityComparer.Default.Equals(x.OriginalDefinition, m.OriginalDefinition));
        return m.IsGenericMethod ? baseM.Construct(m.TypeArguments.Select(SubstType).ToArray()) : baseM;
    }

    static Dictionary<ITypeParameterSymbol, ITypeSymbol>? SubstOf(IMethodSymbol m)
    {
        var map = new Dictionary<ITypeParameterSymbol, ITypeSymbol>(SymbolEqualityComparer.Default);
        if (m.IsGenericMethod)
            for (int i = 0; i < m.TypeArguments.Length; i++) map[m.OriginalDefinition.TypeParameters[i]] = m.TypeArguments[i];
        for (var t = m.ContainingType; t != null; t = t.ContainingType)
            for (int i = 0; i < t.TypeArguments.Length; i++) map[t.OriginalDefinition.TypeParameters[i]] = t.TypeArguments[i];
        return map.Count > 0 ? map : null;
    }

    /// Name part of a type in function prefixes: Box<int> → box_int.
    static string TypeKey(ITypeSymbol t) => t switch
    {
        { SpecialType: SpecialType.System_Int32 } => "int",
        { SpecialType: SpecialType.System_Boolean } => "bool",
        { SpecialType: SpecialType.System_Single } => "milli",
        { SpecialType: SpecialType.System_Double } => "q16",
        { SpecialType: SpecialType.System_String } => "string",
        INamedTypeSymbol { IsGenericType: true } n => Snake(n.Name) + string.Concat(n.TypeArguments.Select(a => "_" + TypeKey(a))),
        IArrayTypeSymbol a => TypeKey(a.ElementType) + "s",
        _ => Snake(t.Name),
    };

    // ================================================================ functions (registration and generic instances)
    FnInfo MakeFn(IMethodSymbol sym, BaseMethodDeclarationSyntax decl, INamedTypeSymbol? thisType = null)
    {
        string prefix = thisType != null ? TypeKey(thisType) + "." + Snake(sym.Name) : MethodPrefix(sym);
        while (fns.Values.Any(f => f.Prefix == prefix) || instances.Values.Any(f => f.Prefix == prefix)) prefix += "_";
        var info = new FnInfo { Sym = sym, Decl = decl, Prefix = prefix, Path = $"{ns}:{prefix.Replace('.', '/')}", Obj = Obj, Subst = SubstOf(sym) };
        var saved = subst;
        subst = info.Subst;
        try
        {
            if (!sym.IsStatic && IsUserClass(sym.ContainingType)) info.ThisObj = AllocObj(thisType ?? sym.ContainingType, $"${prefix}#this", decl);
            var rk = Kind(sym.ReturnType);
            if (rk is VK.Obj or VK.NStr or VK.NArr) info.RetVal = AllocVar(sym.ReturnType, $"${prefix}#ret", decl);
            foreach (var p in sym.Parameters)
                info.Params[p.OriginalDefinition] = Kind(p.Type) switch
                {
                    VK.Int or VK.Bool or VK.Fixed or VK.Double => new ScoreVar(H($"${prefix}.{p.Name}"), Kind(p.Type)),
                    VK.Entity => new EntVar($"mcs.{ns}.{prefix}.{p.Name}") { Spawned = true },
                    VK.Array => Unsup<Var>(decl, $"Array parameters ({p.Name}) are only supported in [Inline] methods — or use a static array field, or NArray<T>."),
                    VK.Ref => new RefVar($"{(ClassOf(p.Type)?.IsPlayer == true ? "@a" : "@e")}[tag=mcs.{ns}.{prefix}.{p.Name}]") { Tag = $"mcs.{ns}.{prefix}.{p.Name}", Type = p.Type },
                    VK.Obj or VK.NStr or VK.NArr => AllocVar(p.Type, $"${prefix}.{p.Name}", decl),
                    _ => Unsup<Var>(decl, $"Unsupported parameter type: {p.Type}"),
                };
        }
        finally { subst = saved; }
        return info;
    }

    /// The function for a call (a generic instance is created on first use and generated later).
    FnInfo? FindFn(IMethodSymbol m)
    {
        if (!IsTemplate(m.OriginalDefinition)) return fns.TryGetValue(m.OriginalDefinition, out var f) ? f : CtorFn(m);
        var c = ConcreteMethod(m);
        if (instances.TryGetValue(c, out var inst)) return inst;
        if (c.OriginalDefinition.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is not BaseMethodDeclarationSyntax decl || HasAttr(c, "InlineAttribute")) return null;
        if (c.TypeArguments.Concat(c.ContainingType.TypeArguments).Any(a => a is ITypeParameterSymbol))
            throw new UnsupportedException(cur.Info.Decl, $"Internal: unresolved type argument in {c}.");
        if (decl.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Any(i => SymbolEqualityComparer.Default.Equals((comp.GetSemanticModel(decl.SyntaxTree).GetSymbolInfo(i).Symbol as IMethodSymbol)?.OriginalDefinition, c.OriginalDefinition)))
            throw new UnsupportedException(decl, $"Recursive generic methods are not supported ({c.Name}).");
        inst = MakeFn(c, decl);
        instances[c] = inst;
        pendingFns.Enqueue(inst);
        return inst;
    }

    /// Constructors of non-generic classes (created on first use).
    FnInfo? CtorFn(IMethodSymbol m)
    {
        if (m.MethodKind != MethodKind.Constructor || !IsUserClass(m.ContainingType)) return null;
        if (instances.TryGetValue(m, out var f)) return f;
        if (m.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is not ConstructorDeclarationSyntax decl) return null;
        f = MakeFn(m, decl);
        instances[m] = f;
        pendingFns.Enqueue(f);
        return f;
    }

    readonly Dictionary<(IMethodSymbol, INamedTypeSymbol), FnInfo> inherited = new();

    /// An instance method called on an object of class `type`. A method inherited from a base class gets its own
    /// copy per class, so calls inside it (this.Sides()) reach that class's overrides.
    FnInfo GetMethodFor(IMethodSymbol m, INamedTypeSymbol type, SyntaxNode at)
    {
        var c = ConcreteMethod(m);
        if (SymbolEqualityComparer.Default.Equals(c.ContainingType, type) || c.MethodKind == MethodKind.Constructor) return GetFn(m, at);
        var key = (c, type);
        if (inherited.TryGetValue(key, out var f)) return f;
        if (c.OriginalDefinition.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is not BaseMethodDeclarationSyntax decl)
            throw new UnsupportedException(at, $"'{m.Name}' cannot be used in MC#.");
        f = MakeFn(c, decl, type);
        inherited[key] = f;
        pendingFns.Enqueue(f);
        return f;
    }

    FnInfo GetFn(IMethodSymbol m, SyntaxNode at) => FindFn(m)
        ?? throw new UnsupportedException(at, $"'{m.ContainingType?.Name}.{m.Name}' cannot be used in MC# (not available inside Minecraft).");

    void GenerateInstances()
    {
        while (pendingFns.Count > 0)
        {
            var f = pendingFns.Dequeue();
            GenFunction(f, comp.GetSemanticModel(f.Decl.SyntaxTree));
        }
    }

    /// Reports layout errors (unsupported field types, a class containing itself) for every non-generic class.
    void CheckClassLayouts()
    {
        foreach (var tree in userTrees)
        {
            var model = comp.GetSemanticModel(tree);
            foreach (var cd in tree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>())
                if (model.GetDeclaredSymbol(cd) is INamedTypeSymbol { IsGenericType: false } t && IsUserClass(t))
                    try { AllocObj(t, "$check", cd); } catch (UnsupportedException e) { Error(e.Node, e.Message); }
        }
    }

    // ================================================================ objects: layout
    /// A variable of a class / NString / NArray type with storage named after `holderBase` ("$main.v").
    Var AllocVar(ITypeSymbol t, string holderBase, SyntaxNode at) => Kind(t) switch
    {
        VK.Obj => AllocObj((INamedTypeSymbol)SubstType(t), holderBase, at),
        VK.NStr => new NStrVar(StoragePathOf(holderBase)),
        VK.NArr => new NArrVar(StoragePathOf(holderBase), ElemKindOf(t)),
        VK.Int or VK.Bool or VK.Fixed or VK.Double => new ScoreVar(H(holderBase), Kind(t)),
        _ => throw new UnsupportedException(at, $"Unsupported type: {t}"),
    };

    static IEnumerable<ISymbol> InstanceFields(INamedTypeSymbol t)
    {
        var chain = new List<INamedTypeSymbol>();
        for (var b = t; b != null && IsUserClass(b); b = b.BaseType) chain.Insert(0, b);
        foreach (var b in chain)
            foreach (var f in b.GetMembers().OfType<IFieldSymbol>())
                if (!f.IsStatic && !f.IsConst) yield return f.AssociatedSymbol ?? f;   // auto-properties: their property
    }

    static ITypeSymbol MemberType(ISymbol m) => m is IPropertySymbol p ? p.Type : ((IFieldSymbol)m).Type;

    ObjVar AllocObj(INamedTypeSymbol type, string @base, SyntaxNode at, int depth = 0)
    {
        if (depth > 8) throw new UnsupportedException(at, $"{type.Name} contains itself (objects in fields are embedded, so a class cannot contain its own type).");
        var o = new ObjVar(type, @base);
        foreach (var f in InstanceFields(type))
        {
            var ft = SubstType(MemberType(f));
            string fb = $"{@base}.{f.Name}";
            o.Fields[f.Name] = Kind(ft) switch
            {
                VK.Int or VK.Bool or VK.Fixed or VK.Double => new ScoreVar(H(fb), Kind(ft)),
                VK.Obj => AllocObj((INamedTypeSymbol)ft, fb, at, depth + 1),
                VK.NStr => new NStrVar(StoragePathOf(fb)),
                VK.NArr => new NArrVar(StoragePathOf(fb), ElemKindOf(ft)),
                VK.Array => new ArrVar(fb, FieldArrayLength(f, at), ElemKind(ft)),
                VK.String => throw new UnsupportedException(FieldSyntax(f) ?? at, $"Instance fields cannot be compile-time strings ({type.Name}.{f.Name}); use NString."),
                _ => throw new UnsupportedException(FieldSyntax(f) ?? at, $"Fields of type {ft} are not supported in classes ({type.Name}.{f.Name})."),
            };
        }
        return o;
    }

    static SyntaxNode? FieldSyntax(ISymbol f) => f.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();

    static ExpressionSyntax? FieldInit(ISymbol f) => FieldSyntax(f) switch
    {
        VariableDeclaratorSyntax v => v.Initializer?.Value,
        PropertyDeclarationSyntax p => p.Initializer?.Value,
        _ => null,
    };

    int FieldArrayLength(ISymbol f, SyntaxNode at)
    {
        var init = FieldInit(f) ?? throw new UnsupportedException(FieldSyntax(f) ?? at, $"Array fields need a size: {f.Name} = new int[10];");
        var (size, items) = ArrayInit(init);
        var model = comp.GetSemanticModel(init.SyntaxTree);
        if (size != null) return CheckLength(model.GetConstantValue(size).Value is int n ? n : throw new UnsupportedException(size, "The array length must be a compile-time constant."), size);
        if (items != null) return CheckLength(items.Count, init);
        throw new UnsupportedException(init, "Array fields need a size: new int[10] or { 1, 2, 3 }.");
    }

    // ================================================================ objects: values
    /// Default values, then the field initializers of the class (run with `this` = o).
    void InitObj(ObjVar o)
    {
        foreach (var f in InstanceFields(o.Type))
        {
            var fv = o.Fields[f.Name];
            var init = FieldInit(f);
            if (init == null || fv is ArrVar) { ResetVar(fv); if (fv is ArrVar fa && init != null) InitArrayField(fa, init); continue; }
            WithModel(init, o, () => AssignVar(fv, init, true));
        }
    }

    void InitArrayField(ArrVar a, ExpressionSyntax init)
    {
        var (_, items) = ArrayInit(init);
        if (items == null) return;
        WithModel(init, null, () => { for (int i = 0; i < items.Count; i++) AssignHolder(new ScoreVar(H($"{a.Base}[{i}]"), a.Elem), items[i]); });
    }

    void ResetVar(Var v)
    {
        switch (v)
        {
            case ScoreVar s: Emit($"scoreboard players set {s.Holder} 0"); break;
            case ObjVar o: foreach (var f in o.Fields.Values) ResetVar(f); break;
            case NStrVar ns2: Emit($"data modify storage {Storage} {ns2.Path} set value \"\""); break;
            case NArrVar na: Emit($"data modify storage {Storage} {na.Path} set value []"); break;
            case ArrVar a: for (int i = 0; i < a.Length; i++) Emit($"scoreboard players set {H($"{a.Base}[{i}]")} 0"); break;
        }
    }

    /// Runs code with the semantic model of another tree (field initializers, ToString bodies) and `this` = obj.
    void WithModel(SyntaxNode node, ObjVar? self, Action a)
    {
        var (m, t) = (cur.Model, cur.ThisObj);
        cur.Model = comp.GetSemanticModel(node.SyntaxTree);
        if (self != null) cur.ThisObj = self;
        try { a(); } finally { cur.Model = m; cur.ThisObj = t; }
    }

    /// dst = value of e. Scores and NStrings are copied; objects / NArrays embedded in dst are overwritten.
    void AssignVar(Var dst, ExpressionSyntax e, bool fresh = false)
    {
        switch (dst)
        {
            case ScoreVar s: AssignHolder(s, e); break;
            case NStrVar s: StrInto(e, s.Path); break;
            case NArrVar a: ArrInto(e, a); break;
            case ObjVar o:
                if (Unparen(e) is BaseObjectCreationExpressionSyntax oc) ConstructInto(o, oc);
                else CopyObj(GetObj(e), o);
                break;
            case ArrVar a:
                var src = GetArr(e);
                for (int i = 0; i < Math.Min(a.Length, src.Length); i++) Emit($"scoreboard players operation {H($"{a.Base}[{i}]")} = {H($"{src.Base}[{i}]")}");
                break;
            default: throw new UnsupportedException(e, "Unsupported assignment.");
        }
    }

    static ExpressionSyntax Unparen(ExpressionSyntax e) => e is ParenthesizedExpressionSyntax p ? Unparen(p.Expression) : e;

    void CopyVar(Var src, Var dst)
    {
        if (ReferenceEquals(src, dst)) return;
        switch (dst)
        {
            case ScoreVar d when src is ScoreVar s: SetHolder(d.Holder, new ScoreOp(s.Holder)); break;
            case ObjVar d when src is ObjVar s: CopyObj(s, d); break;
            case NStrVar d when src is NStrVar s: if (d.Path != s.Path) Emit($"data modify storage {Storage} {d.Path} set from storage {Storage} {s.Path}"); break;
            case NArrVar d when src is NArrVar s: if (d.Path != s.Path) Emit($"data modify storage {Storage} {d.Path} set from storage {Storage} {s.Path}"); break;
            case ArrVar d when src is ArrVar s:
                for (int i = 0; i < Math.Min(d.Length, s.Length); i++) Emit($"scoreboard players operation {H($"{d.Base}[{i}]")} = {H($"{s.Base}[{i}]")}");
                break;
        }
    }

    /// Copies the fields dst has (dst may be a base class frame of src, or the other way round).
    void CopyObj(ObjVar src, ObjVar dst)
    {
        if (ReferenceEquals(src, dst)) return;
        foreach (var (name, d) in dst.Fields)
            if (src.Fields.TryGetValue(name, out var s)) CopyVar(s, d);
    }

    /// `new T(args) { F = v }` built into the object `target`.
    void ConstructInto(ObjVar target, BaseObjectCreationExpressionSyntax oc)
    {
        var ctor = cur.Model.GetSymbolInfo(oc).Symbol as IMethodSymbol ?? throw new UnsupportedException(oc, "Cannot resolve the constructor.");
        var ctorFn = ctor.DeclaringSyntaxReferences.Length > 0 ? GetFn(ctor, oc) : null;
        if (ctorFn != null)
        {
            BindArgs(ctorFn, ctor, oc.ArgumentList?.Arguments ?? default, oc);
            Emit($"function {ctorFn.Path}");
            CopyObj(ctorFn.ThisObj!, target);
            CopyBackArgs(ctorFn, ctor, oc.ArgumentList?.Arguments ?? default);
        }
        else InitObj(target);
        if (oc.Initializer != null)
            foreach (var x in oc.Initializer.Expressions)
            {
                if (x is not AssignmentExpressionSyntax { Left: IdentifierNameSyntax name } a)
                    throw new UnsupportedException(x, "Only field assignments (Name = value) are allowed here.");
                if (!target.Fields.TryGetValue(name.Identifier.ValueText, out var fv)) throw new UnsupportedException(name, $"{target.Type.Name} has no field {name}.");
                AssignVar(fv, a.Right);
            }
    }

    /// The object an expression refers to.
    ObjVar GetObj(ExpressionSyntax e)
    {
        switch (e)
        {
            case ParenthesizedExpressionSyntax p: return GetObj(p.Expression);
            case ThisExpressionSyntax: return cur.ThisObj ?? throw new UnsupportedException(e, "'this' is not an object here.");
            case BaseExpressionSyntax: return cur.ThisObj ?? throw new UnsupportedException(e, "'base' is not an object here.");
            case BaseObjectCreationExpressionSyntax oc:
            {
                var o = AllocObj((INamedTypeSymbol)SubstType(cur.Model.GetTypeInfo(oc).Type!), $"${cur.Info.Prefix}#o{objTemp++}", oc);
                ConstructInto(o, oc);
                return o;
            }
            case InvocationExpressionSyntax inv:
                EmitInvocation(inv, false);
                return CallResult(inv) as ObjVar ?? throw new UnsupportedException(inv, "This call does not return an object.");
            case LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NullLiteralExpression }:
                throw new UnsupportedException(e, "Objects cannot be null in MC# (variables always refer to an object).");
            case ConditionalExpressionSyntax:
                throw new UnsupportedException(e, "Object references are fixed at compile time: '?:' cannot choose between objects. Copy the fields instead.");
        }
        return GetVar(e) as ObjVar ?? throw new UnsupportedException(e, "Expected an object.");
    }

    /// Where a call left its object / NString / NArray result.
    Var? CallResult(InvocationExpressionSyntax inv) =>
        cur.Model.GetSymbolInfo(inv).Symbol is IMethodSymbol m && FindFn(m) is FnInfo f ? f.RetVal : null;

    /// A field of an object (`o.x`, or `x` / `this.x` inside an instance method).
    Var? ObjField(ExpressionSyntax e, ISymbol sym)
    {
        if (sym is not (IFieldSymbol { IsStatic: false } or IPropertySymbol { IsStatic: false, IsIndexer: false })) return null;
        if (!IsUserClass(sym.ContainingType)) return null;
        var recv = e is MemberAccessExpressionSyntax ma ? GetObj(ma.Expression) : cur.ThisObj ?? throw new UnsupportedException(e, "No object here.");
        return recv.Fields.TryGetValue(sym.Name, out var v) ? v
            : throw new UnsupportedException(e, sym is IPropertySymbol ? $"Only auto-properties ({{ get; set; }}) are supported in classes: {sym.Name}." : $"Unknown field {sym.Name}.");
    }

    // ================================================================ objects: declarations and assignment
    void DeclareObjLocal(ILocalSymbol sym, ExpressionSyntax? init, SyntaxNode at)
    {
        if (init == null) return;   // allocated at the first assignment
        cur.Locals[sym] = NewObjValue(sym, init);
    }

    /// For `T x = init` / `x = init`: a new object for `new …` and call results, the same object otherwise.
    ObjVar NewObjValue(ISymbol sym, ExpressionSyntax init)
    {
        var e = Unparen(init);
        if (e is BaseObjectCreationExpressionSyntax oc)
        {
            var o = AllocObj((INamedTypeSymbol)SubstType(cur.Model.GetTypeInfo(oc).Type!), BaseOf(UniqueHolder(sym.Name)), oc);
            ConstructInto(o, oc);
            return o;
        }
        if (e is InvocationExpressionSyntax)
        {
            var src = GetObj(e);
            var o = AllocObj(src.Type, BaseOf(UniqueHolder(sym.Name)), e);
            CopyObj(src, o);
            return o;
        }
        return GetObj(e);
    }

    static string BaseOf(string holder) => holder[..holder.LastIndexOf(' ')];

    /// x = value for object / NArray variables. Locals are rebound (references); fields and parameters are copied into.
    bool EmitRefAssign(AssignmentExpressionSyntax a, ISymbol? sym)
    {
        var k = Kind(cur.Model.GetTypeInfo(a.Left).Type);
        if (k is not (VK.Obj or VK.NArr)) return false;
        if (!a.IsKind(SyntaxKind.SimpleAssignmentExpression)) throw new UnsupportedException(a, "Only '=' works on objects.");
        if (sym is ILocalSymbol local)
        {
            CheckRebind(local, a);
            cur.Locals[local] = k == VK.Obj ? NewObjValue(local, a.Right) : NewArrValue(local, a.Right);
            return true;
        }
        var dst = GetVar(a.Left);
        AssignVar(dst, a.Right);
        return true;
    }

    /// References are fixed at compile time, so a local cannot point to another object depending on a condition.
    void CheckRebind(ILocalSymbol local, SyntaxNode at)
    {
        var decl = local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
        foreach (var anc in at.Ancestors())
        {
            if (anc is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax) break;
            if (anc is IfStatementSyntax or ElseClauseSyntax or WhileStatementSyntax or ForStatementSyntax or DoStatementSyntax
                    or ForEachStatementSyntax or SwitchSectionSyntax or LambdaExpressionSyntax or ConditionalExpressionSyntax
                && (decl == null || !anc.Span.Contains(decl.Span)))
                throw new UnsupportedException(at, $"'{local.Name}' refers to an object fixed at compile time and cannot be pointed at another object inside if/loops/switch. " +
                    "Declare it inside the block, or assign its fields.");
        }
    }

    // ================================================================ calls
    /// Arguments into the callee's parameters (objects, NStrings and NArrays are copied in).
    void BindArgs(FnInfo callee, IMethodSymbol sym, SeparatedSyntaxList<ArgumentSyntax> args, SyntaxNode at)
    {
        for (int i = 0; i < args.Count; i++)
        {
            var pv = callee.Params[sym.Parameters[i].OriginalDefinition];
            switch (pv)
            {
                case ScoreVar sv: AssignHolder(sv, args[i].Expression); break;
                case ObjVar or NStrVar or NArrVar: AssignVar(pv, args[i].Expression); break;
                case EntVar ev:
                    var src = GetEnt(args[i].Expression);
                    Emit($"tag @e[tag={ev.Tag}] remove {ev.Tag}");
                    Emit($"tag {Sel(src)} add {ev.Tag}");
                    break;
                case RefVar rv:
                    Emit($"tag @a remove {rv.Tag}");
                    Emit($"tag {GetRef(args[i].Expression).Selector} add {rv.Tag}");
                    break;
            }
        }
    }

    /// Objects and NArrays passed as arguments are references: copy the callee's changes back.
    void CopyBackArgs(FnInfo callee, IMethodSymbol sym, SeparatedSyntaxList<ArgumentSyntax> args)
    {
        for (int i = 0; i < args.Count; i++)
        {
            var pv = callee.Params[sym.Parameters[i].OriginalDefinition];
            if (pv is not (ObjVar or NArrVar)) continue;
            var e = Unparen(args[i].Expression);
            if (e is BaseObjectCreationExpressionSyntax or InvocationExpressionSyntax) continue;   // a temporary
            CopyVar(pv, pv is ObjVar ? GetObj(e) : GetNArr(e));
        }
    }

    /// An instance method of a plain class: receiver → this frame, call, frame → receiver.
    void EmitObjCall(FnInfo callee, InvocationExpressionSyntax inv, ObjVar recv)
    {
        CopyObj(recv, callee.ThisObj!);
        Emit($"function {callee.Path}");
        if (MutatesThis(callee)) CopyObj(callee.ThisObj!, recv);
    }

    /// The override that runs for an object of class `type` (virtual calls are resolved at compile time).
    static IMethodSymbol Devirtualize(IMethodSymbol m, INamedTypeSymbol type)
    {
        if (!(m.IsVirtual || m.IsAbstract || m.IsOverride)) return m;
        for (var t = type; t != null; t = t.BaseType)
            foreach (var cand in t.GetMembers(m.Name).OfType<IMethodSymbol>())
                for (var o = cand; o != null; o = o.OverriddenMethod)
                    if (SymbolEqualityComparer.Default.Equals(o.OriginalDefinition, m.OriginalDefinition)) return cand;
        return m;
    }

    /// Can the method change its object? (assignments / ++ / -- to fields, calls of other instance methods)
    bool MutatesThis(FnInfo f)
    {
        if (mutatesThis.TryGetValue(f, out var r)) return r;
        mutatesThis[f] = true;   // assume yes while checking (cycles)
        var model = comp.GetSemanticModel(f.Decl.SyntaxTree);
        bool IsMine(ExpressionSyntax t)
        {
            while (t is ElementAccessExpressionSyntax ea) t = ea.Expression;
            while (t is MemberAccessExpressionSyntax ma && ma.Expression is not ThisExpressionSyntax && model.GetSymbolInfo(ma.Expression).Symbol is IFieldSymbol or IPropertySymbol) t = ma.Expression;
            var s = model.GetSymbolInfo(t).Symbol;
            return s is IFieldSymbol { IsStatic: false } or IPropertySymbol { IsStatic: false, IsIndexer: false } && (t is IdentifierNameSyntax || t is MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax })
                || s is IPropertySymbol { IsIndexer: true };
        }
        bool any = f.Decl.DescendantNodes().Any(n => n switch
        {
            AssignmentExpressionSyntax a => IsMine(a.Left),
            PrefixUnaryExpressionSyntax u when u.IsKind(SyntaxKind.PreIncrementExpression) || u.IsKind(SyntaxKind.PreDecrementExpression) => IsMine(u.Operand),
            PostfixUnaryExpressionSyntax u => IsMine(u.Operand),
            InvocationExpressionSyntax i => model.GetSymbolInfo(i).Symbol is IMethodSymbol { IsStatic: false } im && IsUserClass(im.ContainingType)
                && (i.Expression is IdentifierNameSyntax || i.Expression is MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax or BaseExpressionSyntax }
                    || i.Expression is MemberAccessExpressionSyntax mx && IsMine(mx.Expression))
                // Items.Add(x), Items.RemoveAt(0), …: NArray methods change the array in place
                || i.Expression is MemberAccessExpressionSyntax na && IsMine(na.Expression) && IsNArrType(model.GetTypeInfo(na.Expression).Type)
                // this, or a field holding an object / NArray, passed to another method (which may change it)
                || i.ArgumentList.Arguments.Any(x => x.Expression is ThisExpressionSyntax
                    || IsMine(x.Expression) && model.GetTypeInfo(x.Expression).Type is var at && (IsNArrType(at) || IsUserClass(at))),
            _ => false,
        });
        mutatesThis[f] = any;
        return any;
    }

    /// Returns from a method whose result is an object / NString / NArray.
    void EmitRefReturn(ExpressionSyntax e) => AssignVar(cur.Info.RetVal!, e);

    // ================================================================ printing
    List<string> ObjComps(ObjVar o, SyntaxNode at)
    {
        // a ToString() override with a single expression is evaluated with `this` = o
        for (var t = o.Type; t != null && IsUserClass(t); t = t.BaseType)
        {
            var ts = t.GetMembers("ToString").OfType<IMethodSymbol>().FirstOrDefault(m => m.Parameters.Length == 0 && m.IsOverride);
            if (ts?.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is not MethodDeclarationSyntax md) continue;
            var expr = md.ExpressionBody?.Expression ?? (md.Body?.Statements is [ReturnStatementSyntax { Expression: { } re }] ? re : null)
                ?? throw new UnsupportedException(md, "ToString() must be a single expression (=> $\"…\") to be printed.");
            List<string> comps = null!;
            WithModel(expr, o, () => comps = Components(expr));
            return comps;
        }
        var list = new List<string> { Text(o.Type.Name + "(") };
        bool first = true;
        foreach (var (name, v) in o.Fields)
        {
            if (!first) list.Add(Text(", "));
            first = false;
            list.Add(Text(name + ": "));
            list.AddRange(VarComps(v, at));
        }
        list.Add(Text(")"));
        return list;
    }

    List<string> VarComps(Var v, SyntaxNode at) => v switch
    {
        ScoreVar { Kind: VK.Bool } s => BoolComps(Cond.Of($"if score {s.Holder} matches 1")),
        ScoreVar { Kind: VK.Fixed or VK.Double } s => FixedComps(new ScoreOp(s.Holder), s.Kind),
        ScoreVar s => new() { ScoreComp(s.Holder) },
        ObjVar o => ObjComps(o, at),
        NStrVar s => new() { StorageComp(s.Path) },
        NArrVar a => NArrComps(a),
        ArrVar a => [Text("["), .. ArrayComps(a, ", ", at), Text("]")],
        _ => throw new UnsupportedException(at, "This value cannot be printed."),
    };

    // ================================================================ static objects
    void DeclareObjStatic(SemanticModel model, IFieldSymbol f, VariableDeclaratorSyntax v)
    {
        string cls = StaticPrefix(f);
        try
        {
            var t = (INamedTypeSymbol)f.Type;
            var o = AllocObj(t, $"$static.{cls}{f.Name}", v);
            objStatics[f] = o;
            staticObjInits.Add((f, o, v.Initializer?.Value));
        }
        catch (UnsupportedException e) { Error(e.Node, e.Message); }
    }

    /// Static objects are built once (on the first load), like other static fields keep their values.
    void FinishStaticObjects(List<string> load)
    {
        foreach (var (f, o, init) in staticObjInits)
        {
            var dummy = (MethodDeclarationSyntax)SyntaxFactory.ParseMemberDeclaration($"void __sinit() {{ }}")!;
            string prefix = $"__sinit/{Snake(f.Name)}";
            var info = new FnInfo { Sym = AnyMethod(), Decl = dummy, Prefix = prefix.Replace('/', '.'), Path = $"{ns}:{prefix}", Obj = Obj };
            var model = comp.GetSemanticModel(f.DeclaringSyntaxReferences[0].SyntaxTree);
            cur = new FnCtx(info, model);
            try
            {
                if (init == null) InitObj(o);
                else AssignVar(o, init);
            }
            catch (UnsupportedException e) { Error(e.Node, e.Message); }
            string flag = H($"{o.Base}#init");
            cur.Sink.Add($"scoreboard players set {flag} 1");
            pack.Functions[info.Path] = cur.Sink;
            load.Add($"execute unless score {flag} matches 1 run function {info.Path}");
        }
        GenerateInstances();   // constructors first used here
    }

    IMethodSymbol AnyMethod() => comp.GetSpecialType(SpecialType.System_Object).GetMembers("ToString").OfType<IMethodSymbol>().First();
}
