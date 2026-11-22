using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mcs.Compiler;

/// <summary>
/// Syntax rewrites for scoreboard classes (`class test : dummy {}`), done before binding:
///   test.hello            -> declares `public static int hello;` in test (if it is not declared)
///   m.Mount(test, "obj")  -> m.Mount(typeof(test), "obj")
/// Works across files. Only spans inside the touched lines change, so line numbers are preserved.
/// </summary>
public static class MountRewriter
{
    static bool IsDummyClass(ClassDeclarationSyntax c) =>
        c.BaseList?.Types.Any(t => t.Type is IdentifierNameSyntax { Identifier.ValueText: "dummy" }
            or QualifiedNameSyntax { Right.Identifier.ValueText: "dummy" }) == true;

    public static List<SyntaxTree> Apply(List<SyntaxTree> trees)
    {
        var classes = trees.SelectMany(t => t.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>()).Where(IsDummyClass).ToList();
        if (classes.Count == 0) return trees;
        var names = classes.Select(c => c.Identifier.ValueText).ToHashSet();

        // Members used as Class.member, minus the ones already declared (in any part of the class)
        var declared = classes.GroupBy(c => c.Identifier.ValueText).ToDictionary(g => g.Key, g => g.SelectMany(c => c.Members).SelectMany(MemberNames).ToHashSet());
        var used = new Dictionary<string, SortedSet<string>>();
        foreach (var ma in trees.SelectMany(t => t.GetRoot().DescendantNodes().OfType<MemberAccessExpressionSyntax>()))
            if (ma.Expression is IdentifierNameSyntax id && names.Contains(id.Identifier.ValueText) && ma.Name is IdentifierNameSyntax)
            {
                string cls = id.Identifier.ValueText, member = ma.Name.Identifier.ValueText;
                if (!declared[cls].Contains(member)) (used.TryGetValue(cls, out var set) ? set : used[cls] = new()).Add(member);
            }
        var first = classes.GroupBy(c => c.Identifier.ValueText).ToDictionary(g => g.Key, g => g.First());

        return trees.Select(tree =>
        {
            var root = tree.GetRoot();
            var rw = new Rewriter(names, used, first.Values.Where(c => c.SyntaxTree == tree).ToHashSet());
            var newRoot = rw.Visit(root);
            return newRoot == root ? tree : tree.WithRootAndOptions(newRoot, tree.Options);
        }).ToList();
    }

    static IEnumerable<string> MemberNames(MemberDeclarationSyntax m) => m switch
    {
        FieldDeclarationSyntax f => f.Declaration.Variables.Select(v => v.Identifier.ValueText),
        PropertyDeclarationSyntax p => new[] { p.Identifier.ValueText },
        MethodDeclarationSyntax md => new[] { md.Identifier.ValueText },
        _ => [],
    };

    sealed class Rewriter(HashSet<string> names, Dictionary<string, SortedSet<string>> used, HashSet<ClassDeclarationSyntax> hosts) : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node)
        {
            var visited = (ClassDeclarationSyntax)base.VisitClassDeclaration(node)!;
            if (!hosts.Contains(node) || !used.TryGetValue(node.Identifier.ValueText, out var members)) return visited;
            // Inserted without newlines so later lines keep their numbers
            var fields = members.Select(m => SyntaxFactory.ParseMemberDeclaration($"public static int {m}; ")!);
            return visited.AddMembers(fields.ToArray());
        }

        public override SyntaxNode? VisitArgument(ArgumentSyntax node)
        {
            if (node.Expression is IdentifierNameSyntax id && names.Contains(id.Identifier.ValueText)
                && node.Parent?.Parent is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Mount" } })
                return node.WithExpression(SyntaxFactory.TypeOfExpression(SyntaxFactory.IdentifierName(id.Identifier.ValueText)).WithTriviaFrom(id));
            return base.VisitArgument(node);
        }
    }
}
