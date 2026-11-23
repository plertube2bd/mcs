using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mcs.Compiler;

/// Recursion. Every method keeps its locals, parameters and temporaries in fixed scores ($f.x, $f#t0, …), so a
/// call that can come back into the caller (a cycle in the call graph) would overwrite them. For those calls
/// only, the caller's frame is pushed onto a storage stack before the call and popped after it:
///   function <ns>:__frame/f/save       (append {} to <ns>:mcs stack, copy every score of f into it)
///   function <ns>:f
///   function <ns>:__frame/f/restore    (copy them back, remove the entry)
/// The frame is every score of the method except its return value; it is known only after the method has
/// been generated, so the save / restore functions are filled in at the end.
public sealed partial class Codegen
{
    readonly Dictionary<IMethodSymbol, int> sccOf = new(SymbolEqualityComparer.Default);
    readonly HashSet<IMethodSymbol> recursive = new(SymbolEqualityComparer.Default);

    /// Call graph (calls through [Inline] methods count as calls from the method they are inlined into),
    /// then strongly connected components (Tarjan): a method is recursive if it is in a cycle.
    void FindRecursion()
    {
        var edges = new Dictionary<IMethodSymbol, HashSet<IMethodSymbol>>(SymbolEqualityComparer.Default);
        foreach (var f in fns.Values)
        {
            var targets = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
            CollectCalls(f.Decl, comp.GetSemanticModel(f.Decl.SyntaxTree), targets, new(SymbolEqualityComparer.Default));
            edges[f.Sym] = targets;
        }

        int index = 0, comps = 0;
        var idx = new Dictionary<IMethodSymbol, int>(SymbolEqualityComparer.Default);
        var low = new Dictionary<IMethodSymbol, int>(SymbolEqualityComparer.Default);
        var stack = new Stack<IMethodSymbol>();
        var onStack = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        void Visit(IMethodSymbol v)
        {
            idx[v] = low[v] = index++;
            stack.Push(v); onStack.Add(v);
            foreach (var w in edges[v])
            {
                if (!idx.ContainsKey(w)) { Visit(w); low[v] = Math.Min(low[v], low[w]); }
                else if (onStack.Contains(w)) low[v] = Math.Min(low[v], idx[w]);
            }
            if (low[v] != idx[v]) return;
            var members = new List<IMethodSymbol>();
            IMethodSymbol x;
            do { x = stack.Pop(); onStack.Remove(x); sccOf[x] = comps; members.Add(x); } while (!SymbolEqualityComparer.Default.Equals(x, v));
            if (members.Count > 1 || edges[v].Contains(v)) recursive.UnionWith(members);
            comps++;
        }
        foreach (var f in edges.Keys) if (!idx.ContainsKey(f)) Visit(f);
    }

    void CollectCalls(SyntaxNode body, SemanticModel model, HashSet<IMethodSymbol> into, HashSet<IMethodSymbol> inlined)
    {
        foreach (var inv in body.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (model.GetSymbolInfo(inv).Symbol is not IMethodSymbol m) continue;
            var def = m.OriginalDefinition;
            if (fns.ContainsKey(def)) into.Add(def);
            else if (HasAttr(def, "InlineAttribute") && inlined.Add(def)
                     && def.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is MethodDeclarationSyntax md)
                CollectCalls(md, comp.GetSemanticModel(md.SyntaxTree), into, inlined);
        }
    }

    /// A call from the current method that can re-enter it.
    bool IsRecursiveCall(IMethodSymbol callee) =>
        recursive.Contains(cur.Info.Sym) && sccOf.TryGetValue(callee, out var c) && sccOf[cur.Info.Sym] == c;

    readonly HashSet<FnInfo> framed = new();

    /// Save / restore functions of the current method (filled in by FinishFrames).
    string FrameFn(string op)
    {
        framed.Add(cur.Info);
        return $"{ns}:__frame/{cur.Info.Prefix.Replace('.', '/')}/{op}";
    }

    /// The frame is every score of the method (in all its functions) except its return value.
    void FinishFrames(List<string> load)
    {
        foreach (var info in framed)
        {
            var rx = new Regex(@"(?<![^\s])(\$" + Regex.Escape(info.Prefix) + @"[.#]\S*) " + Regex.Escape(Obj) + @"(?!\S)");
            var holders = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var (path, lines) in pack.Functions)
                if (path == info.Path || path.StartsWith(info.Path + "/"))
                    foreach (var l in lines)
                        foreach (Match m in rx.Matches(l)) holders.Add(m.Groups[1].Value);
            holders.Remove($"${info.Prefix}#ret");
            var frame = holders.ToList();
            string dir = $"{ns}:__frame/{info.Prefix.Replace('.', '/')}";
            var save = new List<string> { $"data modify storage {Storage} stack append value {{}}" };
            var restore = new List<string>();
            for (int k = 0; k < frame.Count; k++)
            {
                save.Add($"execute store result storage {Storage} stack[-1].k{k} int 1 run scoreboard players get {frame[k]} {Obj}");
                restore.Add($"execute store result score {frame[k]} {Obj} run data get storage {Storage} stack[-1].k{k}");
            }
            restore.Add($"data remove storage {Storage} stack[-1]");
            // every frame score exists from load on, so saving never reads an unset score
            foreach (var h in frame) load.Add($"scoreboard players add {h} {Obj} 0");
            pack.Functions[$"{dir}/save"] = save;
            pack.Functions[$"{dir}/restore"] = restore;
        }
    }
}
