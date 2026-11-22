using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Mcs.Compiler;

/// <summary>
/// Rewrites MC#-specific syntax into valid C# so Roslyn can parse it. Line numbers are preserved.
///   asm { ... } / asm cmd;            -> __asm("...");
///   use X;                            -> using X;
///   new T : K x;                      -> T x = new T(TType.K);
///   string.join                       -> string.Join
///   top-level m.Mount(...);           -> static readonly bool __mount_N = m.Mount(...);  (a declaration)
///   top-level asm ...                 -> static void __topasm_N() => __asm(...);  (run on load)
///   top-level methods and fields      -> wrapped in a static partial class __Global
/// </summary>
public static class Preprocessor
{
    public const string GlobalClass = "__Global";
    public const string MountFieldPrefix = "__mount_";
    public const string TopAsmPrefix = "__topasm_";

    public static string Process(string src)
    {
        src = src.Replace("\r\n", "\n");
        src = RewriteAsm(src);
        src = RewriteTokens(src);
        src = WrapTopLevel(src);
        return src;
    }

    // ---------------------------------------------------------------- asm
    static bool IsIdent(char c) => char.IsLetterOrDigit(c) || c == '_';

    static string RewriteAsm(string s)
    {
        var sb = new StringBuilder();
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '/') { int e = s.IndexOf('\n', i); e = e < 0 ? s.Length : e; sb.Append(s, i, e - i); i = e; continue; }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '*') { int e = s.IndexOf("*/", i + 2); e = e < 0 ? s.Length : e + 2; sb.Append(s, i, e - i); i = e; continue; }
            if (c == '"' || c == '\'' || (c == '@' || c == '$') && i + 1 < s.Length && (s[i + 1] == '"' || s[i + 1] == '@' || s[i + 1] == '$'))
            {
                int e = SkipLiteral(s, i); sb.Append(s, i, e - i); i = e; continue;
            }
            if (c == 'a' && string.CompareOrdinal(s, i, "asm", 0, 3) == 0
                && (i == 0 || !IsIdent(s[i - 1])) && (i + 3 >= s.Length || !IsIdent(s[i + 3])))
            {
                int j = i + 3;
                while (j < s.Length && char.IsWhiteSpace(s[j])) j++;
                if (j < s.Length && s[j] == '{')
                {
                    int end = MatchAsmBrace(s, j);
                    string body = s.Substring(j + 1, end - j - 1);
                    var cmds = body.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
                    string consumed = s.Substring(i, end + 1 - i);
                    sb.Append("__asm(").Append(Quote(string.Join("\n", cmds))).Append(");");
                    sb.Append('\n', consumed.Count(ch => ch == '\n'));
                    i = end + 1;
                    continue;
                }
                else if (j + 1 < s.Length && s[j] == '$' && (s[j + 1] == '"' || s[j + 1] == '@'))
                {
                    // asm $"... {expr} ..."  ->  __asmf($"...")  (runtime values go through a macro)
                    int e = SkipLiteral(s, j);
                    sb.Append("__asmf(").Append(s, j, e - j).Append(')');
                    int k2 = e;
                    while (k2 < s.Length && (s[k2] == ' ' || s[k2] == '\t')) k2++;
                    if (k2 >= s.Length || s[k2] is '\n' or '}') sb.Append(';');   // allow omitting ';' at the end of the line
                    i = e;
                    continue;
                }
                else if (j < s.Length && s[j] != '(' && s[j] != '=' && s[j] != ';' && s[j] != '.')
                {
                    // One command: ends at ';' or an unmatched ')' outside quotes/brackets, or at the end of the line
                    int e = j, depth = 0; char quote = '\0';
                    for (; e < s.Length && s[e] != '\n'; e++)
                    {
                        char ch = s[e];
                        if (quote != '\0') { if (ch == '\\') e++; else if (ch == quote) quote = '\0'; continue; }
                        if (ch is '"' or '\'') quote = ch;
                        else if (ch is '{' or '[' or '(') depth++;
                        else if (ch is '}' or ']') depth--;
                        else if (ch == ')') { if (depth == 0) break; depth--; }
                        else if (ch == ';' && depth == 0) break;
                    }
                    string cmd = s.Substring(j, e - j).TrimEnd();
                    sb.Append("__asm(").Append(Quote(cmd)).Append(')');
                    if (e >= s.Length || s[e] == '\n') sb.Append(';');
                    i = e;
                    continue;
                }
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }

    static string Quote(string raw) => "\"" + raw.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"";

    /// Finds the closing brace of an asm block. Braces inside command JSON/NBT strings are ignored.
    static int MatchAsmBrace(string s, int open)
    {
        int depth = 0; char quote = '\0';
        for (int k = open; k < s.Length; k++)
        {
            char c = s[k];
            if (quote != '\0') { if (c == '\\') k++; else if (c == quote) quote = '\0'; continue; }
            if (c == '"' || c == '\'') { quote = c; continue; }
            if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return k;
        }
        throw new CompileException("asm block has no closing '}'.");
    }

    /// Skips a C# string/char literal (including interpolated strings with nested holes) and returns the index after it.
    static int SkipLiteral(string s, int i)
    {
        bool verbatim = false, interp = false;
        while (s[i] == '@' || s[i] == '$') { if (s[i] == '@') verbatim = true; else interp = true; i++; }
        char q = s[i]; i++;
        while (i < s.Length)
        {
            char c = s[i];
            if (!verbatim && c == '\\') { i += 2; continue; }
            if (c == q)
            {
                if (verbatim && i + 1 < s.Length && s[i + 1] == q) { i += 2; continue; }
                return i + 1;
            }
            if (interp && c == '{')
            {
                if (i + 1 < s.Length && s[i + 1] == '{') { i += 2; continue; }
                i = SkipHole(s, i + 1); continue;
            }
            i++;
        }
        return i;
    }

    static int SkipHole(string s, int i)
    {
        int depth = 1;
        while (i < s.Length)
        {
            char c = s[i];
            if (c == '"' || c == '\'' || (c == '@' || c == '$') && i + 1 < s.Length && (s[i + 1] == '"' || s[i + 1] == '@' || s[i + 1] == '$'))
            { i = SkipLiteral(s, i); continue; }
            if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return i + 1;
            i++;
        }
        return i;
    }

    // ---------------------------------------------------------------- token rewrites
    static string RewriteTokens(string s)
    {
        var toks = SyntaxFactory.ParseTokens(s).Where(t => !t.IsKind(SyntaxKind.EndOfFileToken)).ToList();
        var edits = new List<(int start, int len, string text)>();
        int depth = 0;
        for (int k = 0; k < toks.Count; k++)
        {
            var t = toks[k];
            if (t.IsKind(SyntaxKind.OpenBraceToken)) depth++;
            else if (t.IsKind(SyntaxKind.CloseBraceToken)) depth--;

            // use A.B;  ->  using A.B;
            if (depth == 0 && t.IsKind(SyntaxKind.IdentifierToken) && t.ValueText == "use"
                && k + 1 < toks.Count && toks[k + 1].IsKind(SyntaxKind.IdentifierToken))
            {
                edits.Add((t.SpanStart, t.Span.Length, "using"));
                continue;
            }

            // top-level asm (already __asm("...");)  ->  a method that the load function calls
            if (depth == 0 && t.IsKind(SyntaxKind.IdentifierToken) && t.ValueText is "__asm" or "__asmf" && k + 1 < toks.Count
                && toks[k + 1].IsKind(SyntaxKind.OpenParenToken)
                && (k == 0 || toks[k - 1].Kind() is SyntaxKind.SemicolonToken or SyntaxKind.CloseBraceToken or SyntaxKind.CloseBracketToken))
            {
                edits.Add((t.SpanStart, 0, $"static void {TopAsmPrefix}{t.SpanStart}() => "));
                continue;
            }

            // top-level m.Mount(...);  ->  a field, so it can live in the class body
            if (depth == 0 && t.IsKind(SyntaxKind.IdentifierToken) && t.ValueText == "m" && k + 3 < toks.Count
                && toks[k + 1].IsKind(SyntaxKind.DotToken) && toks[k + 2].ValueText == "Mount" && toks[k + 3].IsKind(SyntaxKind.OpenParenToken)
                && (k == 0 || toks[k - 1].Kind() is SyntaxKind.SemicolonToken or SyntaxKind.CloseBraceToken or SyntaxKind.CloseBracketToken))
            {
                edits.Add((t.SpanStart, 0, $"static readonly bool {MountFieldPrefix}{t.SpanStart} = "));
                continue;
            }

            // new T : K x;  ->  T x = new T(TType.K);
            if (t.IsKind(SyntaxKind.NewKeyword) && k + 5 < toks.Count
                && toks[k + 1].IsKind(SyntaxKind.IdentifierToken)
                && toks[k + 2].IsKind(SyntaxKind.ColonToken)
                && toks[k + 3].IsKind(SyntaxKind.IdentifierToken)
                && toks[k + 4].IsKind(SyntaxKind.IdentifierToken)
                && toks[k + 5].IsKind(SyntaxKind.SemicolonToken))
            {
                string type = toks[k + 1].ValueText, kind = toks[k + 3].ValueText, name = toks[k + 4].ValueText;
                int start = t.SpanStart, end = toks[k + 5].Span.End;
                // Block : OakStairs → a dedicated block class; others (Entity : Zombie) → TType enum
                edits.Add((start, end - start, type == "Block"
                    ? $"{kind} {name} = new {kind}();"
                    : $"{type} {name} = new {type}({type}Type.{kind});"));
                k += 5;
                continue;
            }

        }
        var sb = new StringBuilder(s);
        foreach (var e in edits.OrderByDescending(e => e.start)) sb.Remove(e.start, e.len).Insert(e.start, e.text);
        // string.join -> string.Join (lowercase alias; also applies inside interpolation holes)
        return System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\bstring(\s*\.\s*)join\b", "string${1}Join");
    }

    // ---------------------------------------------------------------- top-level wrapping
    static string WrapTopLevel(string s)
    {
        var toks = SyntaxFactory.ParseTokens(s).Where(t => !t.IsKind(SyntaxKind.EndOfFileToken)).ToList();
        if (toks.Any(t => t.IsKind(SyntaxKind.NamespaceKeyword))) return s; // explicit namespaces: leave as-is

        // Insert the wrapper header after the leading using directives
        int insertAt = 0, k = 0;
        while (k < toks.Count)
        {
            int j = k;
            if (toks[j].IsKind(SyntaxKind.GlobalKeyword)) j++;
            if (j < toks.Count && toks[j].IsKind(SyntaxKind.UsingKeyword))
            {
                while (j < toks.Count && !toks[j].IsKind(SyntaxKind.SemicolonToken)) j++;
                if (j >= toks.Count) break;
                insertAt = toks[j].Span.End;
                k = j + 1;
            }
            else break;
        }
        if (insertAt == 0)   // no usings: the header gets its own line, and #line keeps user line/column numbers exact
            return $"public static partial class {GlobalClass} {{\n#line 1\n" + s + "\n}\n";
        return s[..insertAt] + $" public static partial class {GlobalClass} {{" + s[insertAt..] + "\n}\n";
    }
}

public class CompileException(string message) : Exception(message);
