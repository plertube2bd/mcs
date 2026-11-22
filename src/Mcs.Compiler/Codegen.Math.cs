using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mcs.Compiler;

/// Q15.16 helpers (double × / ÷) and MathLib (Sin/Cos/Tan via a lookup table).
/// The helpers are shared mcfunctions under <ns>:__math/, emitted only when used.
public sealed partial class Codegen
{
    readonly HashSet<string> mathFns = new();

    /// Shared math register: "$__m.<name> <objective>"
    string MH(string name) => H($"$__m.{name}");

    string MathFn(string name) { mathFns.Add(name); return $"{ns}:__math/{name}"; }

    /// h = h (op) r via a helper that takes $__m.a, $__m.b and leaves the result in $__m.a.
    void CallMath2(string fn, string h, Operand r)
    {
        Emit($"scoreboard players operation {MH("a")} = {h}");
        SetHolder(MH("b"), r);
        Emit($"function {MathFn(fn)}");
        Emit($"scoreboard players operation {h} = {MH("a")}");
    }

    // 2π in Q15.16 = 411774 + 54545/65536 ; table steps (1024 per turn) per radian = 162.97466… (Q15.16)
    const int TwoPiInt = 411774, TwoPiFrac = 54545, RadToStep = 10680707;
    const int LutSize = 256;                     // entries per quarter wave (1024 per full turn)
    const int Quarter = LutSize * 65536;         // one quarter turn in phase units (Q15.16 table steps)

    Operand EmitMath(string name, InvocationExpressionSyntax inv)
    {
        var sym = (IMethodSymbol)cur.Model.GetSymbolInfo(inv).Symbol!;
        var rk = Kind(sym.ReturnType);
        if (name is "math.floordiv" or "math.floormod")
        {
            var l = EvalNum(inv.ArgumentList.Arguments[0].Expression, VK.Int);
            var r = EvalNum(inv.ArgumentList.Arguments[1].Expression, VK.Int);
            if (l is ConstOp lc && r is ConstOp rc && rc.Value != 0)
                return new ConstOp(name == "math.floordiv" ? (int)FloorDiv(lc.Value, rc.Value) : (int)(lc.Value - FloorDiv(lc.Value, rc.Value) * rc.Value));
            string t = ToTemp(l);
            Emit($"scoreboard players operation {t} {(name == "math.floordiv" ? "/=" : "%=")} {Holder(r)}");
            return new ScoreOp(t);
        }
        var x = EvalNum(inv.ArgumentList.Arguments[0].Expression, VK.Double);
        if (x is ConstOp c)
        {
            double v = c.Value / (double)DScale;
            double r = name switch { "math.sin" => Math.Sin(v), "math.cos" => Math.Cos(v), _ => Math.Tan(v) };
            return new ConstOp(ScaleConst(r, rk));
        }
        if (UseCompute)
        {
            Emit($"execute store result score {MH("r")} run compute default float {TrigExpr(name[5..], x)} 65536");
            return Convert(new ScoreOp(ToTemp(new ScoreOp(MH("r")))), VK.Double, rk, inv);
        }
        SetHolder(MH("x"), x);
        Emit($"function {MathFn(name[5..])}");
        return Convert(new ScoreOp(ToTemp(new ScoreOp(MH("r")))), VK.Double, rk, inv);
    }

    void FinishMath()
    {
        if (mathFns.Count == 0) return;
        string S(string n) => MH(n);
        string C(int v) => ConstHolder(v);
        string Op(string a, string op, string b) => $"scoreboard players operation {a} {op} {b}";
        var fns = new Dictionary<string, List<string>>();

        // Dependencies
        if (mathFns.Contains("tan")) { mathFns.Add("ddiv"); }
        if (mathFns.Overlaps(new[] { "sin", "cos", "tan" })) { mathFns.Add("phase"); mathFns.Add("sinp"); }
        if (mathFns.Contains("phase")) mathFns.Add("dmul");

        if (mathFns.Contains("dmul"))
        {
            // a = floor(a * b / 65536), exact for any result that fits in 32 bits.
            // a = ah·2^16 + al, b = bh·2^16 + bl  (al, bl ∈ [0, 65536))
            // a·b/2^16 = ah·bh·2^16 + ah·bl + al·bh + al·bl/2^16, and al·bl/2^16 = ((al/256)·bl + (al%256)·bl/256) / 256
            fns["dmul"] = new()
            {
                Op(S("ah"), "=", S("a")), Op(S("ah"), "/=", C(65536)),
                Op(S("al"), "=", S("a")), Op(S("al"), "%=", C(65536)),
                Op(S("bh"), "=", S("b")), Op(S("bh"), "/=", C(65536)),
                Op(S("bl"), "=", S("b")), Op(S("bl"), "%=", C(65536)),
                Op(S("a1"), "=", S("al")), Op(S("a1"), "/=", C(256)),
                Op(S("a0"), "=", S("al")), Op(S("a0"), "%=", C(256)),
                Op(S("a1"), "*=", S("bl")),
                Op(S("a0"), "*=", S("bl")), Op(S("a0"), "/=", C(256)),
                Op(S("a1"), "+=", S("a0")), Op(S("a1"), "/=", C(256)),
                Op(S("a"), "=", S("ah")), Op(S("a"), "*=", S("bh")), Op(S("a"), "*=", C(65536)),
                Op(S("ah"), "*=", S("bl")), Op(S("a"), "+=", S("ah")),
                Op(S("al"), "*=", S("bh")), Op(S("a"), "+=", S("al")),
                Op(S("a"), "+=", S("a1")),
            };
        }
        if (mathFns.Contains("ddiv"))
        {
            // a = a * 65536 / b rounded toward zero (like C#), by long division on |a|, |b| (16 fraction bits, no overflow).
            // Saturates to ±2147483647 when the result does not fit (or b == 0).
            fns["ddiv"] = new()
            {
                $"scoreboard players set {S("neg")} 0",
                $"execute if score {S("a")} matches ..-1 run scoreboard players add {S("neg")} 1",
                $"execute if score {S("a")} matches ..-1 run {Op(S("a"), "*=", C(-1))}",
                $"execute if score {S("b")} matches ..-1 run scoreboard players add {S("neg")} 1",
                $"execute if score {S("b")} matches ..-1 run {Op(S("b"), "*=", C(-1))}",
                $"execute if score {S("b")} matches 0 if score {S("a")} matches 1.. run scoreboard players set {S("a")} 2147483647",
                $"execute unless score {S("b")} matches 0 run function {ns}:__math/ddiv_u",
                $"execute if score {S("neg")} matches 1 run {Op(S("a"), "*=", C(-1))}",
            };
            fns["ddiv_u"] = new()
            {
                Op(S("q"), "=", S("a")), Op(S("q"), "/=", S("b")),
                Op(S("r"), "=", S("a")), Op(S("r"), "%=", S("b")),
                $"execute if score {S("q")} matches 32768.. run scoreboard players set {S("a")} 2147483647",
                $"execute if score {S("q")} matches ..32767 run function {ns}:__math/ddiv_f",
            };
            var f = new List<string> { $"scoreboard players set {S("f")} 0" };
            for (int i = 0; i < 16; i++)
            {
                // next bit: 2r >= b  ⇔  r >= b - r   (avoids overflowing 2r)
                f.Add(Op(S("f"), "+=", S("f")));
                f.Add(Op(S("d"), "=", S("b")));
                f.Add(Op(S("d"), "-=", S("r")));
                f.Add($"execute store success score {S("c")} if score {S("r")} >= {S("d")}");
                f.Add(Op(S("f"), "+=", S("c")));
                f.Add($"execute if score {S("c")} matches 1 run {Op(S("r"), "-=", S("d"))}");
                f.Add($"execute if score {S("c")} matches 0 run {Op(S("r"), "+=", S("r"))}");
            }
            f.Add(Op(S("a"), "=", S("q")));
            f.Add(Op(S("a"), "*=", C(65536)));
            f.Add(Op(S("a"), "+=", S("f")));
            fns["ddiv_f"] = f;
            if (UseCompute) { fns["ddiv_slow"] = fns["ddiv"]; fns["ddiv"] = ComputeDDiv($"{ns}:__math/ddiv_slow"); }
        }
        foreach (var (fn, op) in new[] { ("idiv", "/="), ("irem", "%=") })
        {
            if (!mathFns.Contains(fn)) continue;
            // C# integer division / remainder: work on |a|, |b|, then restore the sign
            // (quotient: negative when the signs differ; remainder: sign of a).
            // b == 0 leaves a unchanged (Minecraft cannot throw DivideByZeroException).
            var cmds = new List<string>
            {
                $"scoreboard players set {S("neg")} 0",
                $"execute if score {S("a")} matches ..-1 run scoreboard players add {S("neg")} 1",
                $"execute if score {S("a")} matches ..-1 run {Op(S("a"), "*=", C(-1))}",
            };
            if (fn == "idiv") cmds.Add($"execute if score {S("b")} matches ..-1 run scoreboard players add {S("neg")} 1");
            cmds.Add($"execute if score {S("b")} matches ..-1 run {Op(S("b"), "*=", C(-1))}");
            cmds.Add($"execute unless score {S("b")} matches 0 run {Op(S("a"), op, S("b"))}");
            cmds.Add($"execute if score {S("neg")} matches 1 run {Op(S("a"), "*=", C(-1))}");
            fns[fn] = cmds;
        }
        if (mathFns.Contains("phase"))
        {
            // x (Q15.16 radians) → a = phase in table steps (Q15.16, 1024 steps per turn).
            // Reduce by 2π first (2π = 411774 + 54545/65536 in Q15.16) so large angles stay accurate.
            fns["phase"] = new()
            {
                Op(S("k"), "=", S("x")), Op(S("k"), "/=", C(TwoPiInt + 1)),
                Op(S("a"), "=", S("k")), Op(S("a"), "*=", C(TwoPiInt)),
                Op(S("x"), "-=", S("a")),
                Op(S("k"), "*=", C(TwoPiFrac)), Op(S("k"), "/=", C(65536)),
                Op(S("x"), "-=", S("k")),
                Op(S("a"), "=", S("x")),
                $"scoreboard players set {S("b")} {RadToStep}",
                $"function {ns}:__math/dmul",
            };
        }
        if (mathFns.Contains("sinp"))
        {
            // a = phase → r = sin (Q15.16). Quarter-wave table + symmetry + linear interpolation.
            fns["sinp"] = new()
            {
                Op(S("a"), "%=", C(4 * Quarter)),
                Op(S("q"), "=", S("a")), Op(S("q"), "/=", C(Quarter)),
                Op(S("s"), "=", S("a")), Op(S("s"), "%=", C(Quarter)),
                // quadrants 1 and 3 run the table backwards
                Op(S("o"), "=", S("q")), Op(S("o"), "%=", C(2)),
                $"execute if score {S("o")} matches 1 run {Op(S("s"), "*=", C(-1))}",
                $"execute if score {S("o")} matches 1 run scoreboard players add {S("s")} {Quarter}",
                Op(S("i"), "=", S("s")), Op(S("i"), "/=", C(65536)),
                Op(S("fr"), "=", S("s")), Op(S("fr"), "%=", C(65536)),
                $"function {ns}:__math/lut/0_{LutSize}",
                // packed entry: value·1024 + (next − value)
                Op(S("r"), "=", S("p")), Op(S("r"), "/=", C(1024)),
                Op(S("p"), "%=", C(1024)),
                Op(S("p"), "*=", S("fr")), Op(S("p"), "/=", C(65536)),
                Op(S("r"), "+=", S("p")),
                $"execute if score {S("q")} matches 2.. run {Op(S("r"), "*=", C(-1))}",
            };
            var table = Enumerable.Range(0, LutSize + 1).Select(k => (int)Math.Round(Math.Sin(k * Math.PI / (2 * LutSize)) * DScale)).ToArray();
            int Packed(int k) => table[k] * 1024 + (k < LutSize ? table[k + 1] - table[k] : 0);
            // Binary dispatch tree: works on every version (no macros needed)
            void Node(int lo, int hi)
            {
                var cmds = new List<string>();
                if (hi - lo < 16)
                    for (int k = lo; k <= hi; k++) cmds.Add($"execute if score {S("i")} matches {k} run scoreboard players set {S("p")} {Packed(k)}");
                else
                {
                    int mid = (lo + hi) / 2;
                    cmds.Add($"execute if score {S("i")} matches ..{mid} run function {ns}:__math/lut/{lo}_{mid}");
                    cmds.Add($"execute if score {S("i")} matches {mid + 1}.. run function {ns}:__math/lut/{mid + 1}_{hi}");
                    Node(lo, mid); Node(mid + 1, hi);
                }
                fns[$"lut/{lo}_{hi}"] = cmds;
            }
            Node(0, LutSize);
        }
        if (mathFns.Contains("sin")) fns["sin"] = new() { $"function {ns}:__math/phase", $"function {ns}:__math/sinp" };
        if (mathFns.Contains("cos")) fns["cos"] = new() { $"function {ns}:__math/phase", $"scoreboard players add {S("a")} {Quarter}", $"function {ns}:__math/sinp" };
        if (mathFns.Contains("tan"))
            fns["tan"] = new()
            {
                $"function {ns}:__math/phase",
                Op(S("t"), "=", S("a")),
                $"function {ns}:__math/sinp",
                Op(S("sn"), "=", S("r")),
                Op(S("a"), "=", S("t")), $"scoreboard players add {S("a")} {Quarter}",
                $"function {ns}:__math/sinp",
                Op(S("a"), "=", S("sn")), Op(S("b"), "=", S("r")),
                $"function {ns}:__math/ddiv",
                Op(S("r"), "=", S("a")),
            };
        foreach (var (name, cmds) in fns) pack.Functions[$"{ns}:__math/{name}"] = cmds;
    }
}
