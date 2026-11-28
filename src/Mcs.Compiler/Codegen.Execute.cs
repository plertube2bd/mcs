using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mcs.Compiler;

/// Fluent /execute: Execute.AsPlayerObject(p).At(p).IfBlock("stone", pos).Run(() => { ... }).
/// The chain becomes one "execute <subcommands> run <lambda>" command. When a subcommand needs commands of its own
/// (a runtime bool condition, a block test at an entity-relative position, an entity query), the chain is split
/// there: the rest goes into a sub-function, so those commands run in the context built so far (e.g. once per As target).
public sealed partial class Codegen
{
    Operand? EmitExecuteChain(InvocationExpressionSyntax run)
    {
        var steps = new List<(string name, InvocationExpressionSyntax inv)>();
        ExpressionSyntax e = run;
        while (true)
        {
            if (Unparen(e) is not InvocationExpressionSyntax si || cur.Model.GetSymbolInfo(si).Symbol is not IMethodSymbol sm
                || Intrinsic(sm) is not string n || !n.StartsWith("xc."))
                throw new UnsupportedException(e, "Write an Execute chain in one expression: Execute.As(...).At(...).Run(() => { ... }).");
            steps.Insert(0, (n, si));
            if (sm.IsStatic) break;
            e = ((MemberAccessExpressionSyntax)si.Expression).Expression;
        }
        int depth = cur.Depth;
        try { EmitChain(steps, 0, new(), new()); }
        finally { cur.Depth = depth; }
        return null;
    }

    void EmitChain(List<(string name, InvocationExpressionSyntax inv)> steps, int i, List<string> ctx, List<Pos> ps)
    {
        for (; i < steps.Count; i++)
        {
            var (name, inv) = steps[i];
            if (name == "xc.run")
            {
                string inner = ActionCommand(inv.ArgumentList.Arguments[0].Expression, false);
                if (ctx.Count == 0) { Emit(inner); return; }
                string c = string.Join(" ", ctx);
                EmitCmd(inner.StartsWith("execute ") ? $"execute {c} {inner[8..]}" : $"execute {c} run {inner}", inv, ps.ToArray());
                return;
            }
            string? part = null;
            Pos? pos = null;
            bool never = false;
            var pre = Capture(() => part = ChainPart(name, inv, ref pos, ref never));
            if (never) return;   // a constant-false condition: nothing runs
            if (pre.Count > 0 && ctx.Count > 0)
            {
                var rest = new List<string>(); var restPs = new List<Pos>();
                if (part!.Length > 0) rest.Add(part);
                if (pos != null) restPs.Add(pos);
                var sub = Capture(() => { cur.Sink.AddRange(pre); EmitChain(steps, i + 1, rest, restPs); });
                if (sub.Count > 0) EmitCmd($"execute {string.Join(" ", ctx)} run function {NewSub("exec", sub)}", inv, ps.ToArray());
                return;
            }
            cur.Sink.AddRange(pre);
            if (part!.Length > 0) ctx.Add(part);
            if (pos != null) ps.Add(pos);
        }
        throw new UnsupportedException(steps[^1].inv, "An Execute chain must end with .Run(...).");
    }

    /// One subcommand of an Execute chain (commands it needs go to the current sink).
    string ChainPart(string name, InvocationExpressionSyntax inv, ref Pos? pos, ref bool never)
    {
        var args = inv.ArgumentList.Arguments;
        ExpressionSyntax Arg(int i) => args[i].Expression;
        switch (name)
        {
            case "xc.as": { string s = AnySel(Arg(0)); cur.Depth++; return $"as {s}"; }
            case "xc.at": return $"at {AnySel(Arg(0))}";
            case "xc.rotatedas": return $"rotated as {AnySel(Arg(0))}";
            case "xc.facingent": return $"facing entity {SingleSel(Arg(0))} eyes";
            case "xc.ifentity": return $"if entity {AnySel(Arg(0))}";
            case "xc.unlessentity": return $"unless entity {AnySel(Arg(0))}";
            case "xc.in": return $"in {DimOf(Arg(0))}";
            case "xc.eyes": return "anchored eyes";
            case "xc.align": return $"align {ConstStr(Arg(0))}";
            case "xc.raw": return ConstStr(Arg(0)).Trim();
            case "xc.rotated": return $"rotated {Fmt(ConstDouble(Arg(0), "Rotated angles"))} {Fmt(ConstDouble(Arg(1), "Rotated angles"))}";
            case "xc.pos":
            {
                var p = EvalPos(Arg(0));
                if (p.Args.Length > 0) pos = p;
                return PosContext(p);
            }
            case "xc.facing":
            {
                var p = EvalPos(Arg(0));
                if (p.Prefix.Length == 0) { if (p.Args.Length > 0) pos = p; return $"facing {p.Coords}"; }
                if (p.Prefix.StartsWith("at ") && p.Coords == "~ ~ ~" && p.Args.Length == 0) return $"facing entity {p.Prefix[3..]} feet";
                throw new UnsupportedException(Arg(0), "Facing needs an absolute position, Position.Here offsets or an entity's position.");
            }
            case "xc.ifblock" or "xc.ifblockobj" or "xc.unlessblock" or "xc.unlessblockobj":
            {
                string block = BlockArg(Arg(0), name.EndsWith("obj"));
                string kw = name.StartsWith("xc.if") ? "if" : "unless";
                var p = EvalPos(Arg(1));
                if (p.Prefix.Length == 0)
                {
                    if (p.Args.Length > 0) pos = p;
                    return $"{kw} block {p.Coords} {block}";
                }
                // "at <entity>" would move the rest of the chain: test it on its own first
                string t = Temp();
                Emit($"scoreboard players set {t} 0");
                EmitCmd($"execute store success score {t} {p.Prefix} {kw} block {p.Coords} {block}", inv, p);
                return $"if score {t} matches 1";
            }
            case "xc.if" or "xc.unless":
            {
                var c = EvalCond(Arg(0));
                bool neg = name == "xc.unless";
                if (c.Const is bool b) { if (b == neg) never = true; return ""; }
                if (!neg) return string.Join(" ", c.Parts);
                return $"unless score {Materialize(c)} matches 1";
            }
        }
        throw new UnsupportedException(inv, $"Cannot use '{name}' here.");
    }
}
