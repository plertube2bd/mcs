using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mcs.Compiler;

/// m.Mount: binds static int/bool fields to existing scores, and scoreboard classes (class X : dummy) to objectives.
/// Bindings are compile-time and global: they apply to every use of the field, wherever Mount is written.
public sealed partial class Codegen
{
    readonly Dictionary<IFieldSymbol, string> mounts = new(SymbolEqualityComparer.Default);           // field → "holder objective"
    readonly Dictionary<INamedTypeSymbol, string> classMounts = new(SymbolEqualityComparer.Default);  // dummy class → objective
    readonly SortedSet<string> mountObjectives = new();                                                // created on load

    static bool IsDummyClass(ITypeSymbol? t)
    {
        for (var b = (t as INamedTypeSymbol)?.BaseType; b != null; b = b.BaseType)
            if (b.Name == "dummy" && b.ContainingNamespace?.ToDisplayString() == "MCS") return true;
        return false;
    }

    static readonly Regex ObjectiveName = new("^[A-Za-z0-9_.+-]+$");

    string ObjectiveOfClass(INamedTypeSymbol t) => classMounts.TryGetValue(t, out var o) ? o : $"{ns}.{Snake(t.Name)}";

    void CollectMounts()
    {
        foreach (var tree in userTrees)
        {
            var model = comp.GetSemanticModel(tree);
            foreach (var inv in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (Intrinsic(model.GetSymbolInfo(inv).Symbol) != "mount") continue;
                try { CollectMount(model, inv); }
                catch (UnsupportedException e) { Error(e.Node, e.Message); }
            }
        }
    }

    void CollectMount(SemanticModel model, InvocationExpressionSyntax inv)
    {
        var args = inv.ArgumentList.Arguments;
        string Str(int i) => model.GetConstantValue(args[i].Expression).Value as string
            ?? throw new UnsupportedException(args[i], "Mount needs a string constant here.");

        if (args[0].Expression is TypeOfExpressionSyntax tof)
        {
            var t = model.GetTypeInfo(tof.Type).Type as INamedTypeSymbol;
            if (!IsDummyClass(t)) throw new UnsupportedException(args[0], "Only scoreboard classes (class X : dummy) can be mounted to an objective.");
            string obj = Str(1);
            if (!ObjectiveName.IsMatch(obj)) throw new UnsupportedException(args[1], $"Invalid objective name '{obj}'.");
            if (classMounts.TryGetValue(t!, out var old) && old != obj) throw new UnsupportedException(inv, $"{t!.Name} is already mounted to '{old}'.");
            classMounts[t!] = obj;
            mountObjectives.Add(obj);
            return;
        }

        if (model.GetSymbolInfo(args[0].Expression).Symbol is not IFieldSymbol { IsStatic: true } f || f.IsConst)
            throw new UnsupportedException(args[0], "Mount needs a static int or bool field (e.g. static int hello;).");
        string objective, holder;
        if (args.Count == 3) { objective = Str(1); holder = Str(2); }
        else
        {
            string target = Str(1);
            int dot = target.LastIndexOf('.');
            if (dot <= 0 || dot == target.Length - 1)
                throw new UnsupportedException(args[1], $"Expected \"objective.holder\" (e.g. \"test.hello\"), got \"{target}\". Use Mount(x, objective, holder) if the holder contains a '.'.");
            (objective, holder) = (target[..dot], target[(dot + 1)..]);
        }
        if (!ObjectiveName.IsMatch(objective)) throw new UnsupportedException(args[1], $"Invalid objective name '{objective}'.");
        if (holder.Length == 0 || holder.Any(char.IsWhiteSpace)) throw new UnsupportedException(args[^1], $"Invalid score holder '{holder}' (no spaces).");
        string h = $"{holder} {objective}";
        if (mounts.TryGetValue(f, out var prev) && prev != h) throw new UnsupportedException(inv, $"{f.Name} is already mounted to '{prev}'.");
        mounts[f] = h;
    }

    void DeclareMounted(SemanticModel model, IFieldSymbol f, VariableDeclaratorSyntax v)
    {
        var k = Kind(f.Type);
        if (k is not (VK.Int or VK.Bool)) { Error(v, $"Mounted fields must be int or bool (scores are integers), not {f.Type}."); return; }
        string h;
        if (mounts.TryGetValue(f, out var m)) h = m;
        else
        {
            string obj = ObjectiveOfClass(f.ContainingType);
            mountObjectives.Add(obj);
            h = $"{f.Name} {obj}";
        }
        if (IsDummyClass(f.ContainingType)) mountObjectives.Add(ObjectiveOfClass(f.ContainingType));
        var sv = new ScoreVar(h, k);
        statics[f] = sv;
        // A mounted score belongs to someone else: only an explicit initializer sets it (and only if it has no value yet)
        if (v.Initializer != null)
        {
            var cv = model.GetConstantValue(v.Initializer.Value);
            if (!cv.HasValue) Error(v.Initializer, "Static field initializers must be compile-time constants.");
            else staticInits.Add((sv, ScaleConst(cv.Value, k)));
        }
    }
}
