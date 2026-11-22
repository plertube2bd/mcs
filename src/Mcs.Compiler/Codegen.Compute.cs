namespace Mcs.Compiler;

/// 26.3+: `compute` evaluates a number-provider expression in a single command, so arithmetic that needs a
/// helper function on older versions becomes one command. Semantics (checked against the 26.3 server):
///   - int providers compute in 64 bits per node and fail when a node's result does not fit in an int
///     (div = Java idiv: rounds toward zero; mod = Java %: sign of the dividend; floor_div / floor_mod)
///   - float providers are 32-bit floats; sin/cos use the game's 65536-entry table (Mth.sin)
///   - the command result is floor(value × scale); a failure (overflow, / 0, NaN) makes `store result` write 0
public sealed partial class Codegen
{
    /// One int/float number-provider expression, written as SNBT.
    static class P
    {
        public static string Num(int v) => v.ToString();
        public static string Score(string holder)
        {
            int sp = holder.IndexOf(' ');
            return $"{{type:\"minecraft:score\",target:{{type:\"fixed\",name:{Json.Str(holder[..sp])}}},score:{Json.Str(holder[(sp + 1)..])}}}";
        }
        public static string Bin(string type, string l, string r) => $"{{type:\"minecraft:{type}\",left:{l},right:{r}}}";
        public static string List(string type, params string[] inputs) => $"{{type:\"minecraft:{type}\",inputs:[{string.Join(",", inputs)}]}}";
        public static string Un(string type, string input) => $"{{type:\"minecraft:{type}\",input:{input}}}";
    }

    bool UseCompute => prof.Compute;

    /// The provider for an operand: a literal for constants, a score read otherwise.
    string PInt(Operand op) => op is ConstOp c ? P.Num(c.Value) : P.Score(((ScoreOp)op).Holder);

    void ComputeInt(string target, string expr, string? guard = null) =>
        Emit($"execute {(guard != null ? guard + " " : "")}store result score {target} run compute default integer {expr}");

    /// h = h / r or h % r (C# semantics). A zero divisor leaves h unchanged, like the helper functions.
    void ComputeTrunc(string h, char op, Operand r)
    {
        string expr = P.Bin(op == '/' ? "div" : "mod", P.Score(h), PInt(r));
        ComputeInt(h, expr, r is ScoreOp s ? $"unless score {s.Holder} matches 0" : null);
    }

    /// floor(a · b / 65536) for Q15.16 values, exact (same result as __math/dmul) whenever the result fits.
    /// a = ah·2^16 + al, b = bh·2^16 + bl (al, bl ∈ [0, 65536)):
    ///   a·b/2^16 = 4·(16384·ah·bh) + ah·bl + al·bh + floor((al·(bl/256) + floor(al·(bl%256)/256)) / 256)
    /// Every node stays inside an int as long as the result does (the 2^16·ah·bh term is split in four).
    string DMulExpr(Operand a, Operand b)
    {
        (string hi, string lo, string loHi, string loLo) Parts(Operand o)
        {
            if (o is ConstOp c)
            {
                int hiV = (int)FloorDiv(c.Value, 65536), loV = c.Value - hiV * 65536;
                return (P.Num(hiV), P.Num(loV), P.Num(loV / 256), P.Num(loV % 256));
            }
            string s = PInt(o), lo = P.Bin("floor_mod", s, "65536");
            return (P.Bin("floor_div", s, "65536"), lo, P.Bin("floor_div", lo, "256"), P.Bin("floor_mod", s, "256"));
        }
        var (ah, al, _, _) = Parts(a);
        var (bh, bl, blh, bll) = Parts(b);
        string quarter = P.List("mul", "16384", ah, bh);
        string frac = P.Bin("floor_div", P.List("add", P.List("mul", al, blh), P.Bin("floor_div", P.List("mul", al, bll), "256")), "256");
        return P.List("add", quarter, quarter, quarter, quarter, P.List("mul", ah, bl), P.List("mul", al, bh), frac);
    }

    /// __math/ddiv for 26.3+: a = a·65536 / b (toward zero, saturating) in a few commands when |b| < 2^23
    /// (b < 128.0); larger divisors use the bit-by-bit version (__math/ddiv_slow).
    List<string> ComputeDDiv(string slowFn)
    {
        string a = MH("a"), b = MH("b"), q = MH("q");
        string A = P.Score(a), B = P.Score(b), Q = P.Score(q);
        string r1 = P.List("mul", P.Bin("mod", A, B), "256");          // r·256, |r| < |b| < 2^23
        string r2 = P.List("mul", P.Bin("mod", r1, B), "256");
        string sum = P.List("add", P.List("mul", Q, "65536"), P.List("mul", P.Bin("div", r1, B), "256"), P.Bin("div", r2, B));
        return new()
        {
            $"execute if score {b} matches 0 if score {a} matches 1.. run return run scoreboard players set {a} 2147483647",
            $"execute if score {b} matches 0 if score {a} matches ..-1 run return run scoreboard players set {a} -2147483647",
            $"execute if score {b} matches 0 run return 0",
            $"execute unless score {b} matches -8388607..8388607 run return run function {slowFn}",
            $"execute store result score {q} run compute default integer {P.Bin("div", A, B)}",
            $"execute if score {q} matches 32768.. run return run scoreboard players set {a} 2147483647",
            $"execute if score {q} matches ..-32768 run return run scoreboard players set {a} -2147483647",
            $"execute store result score {a} run compute default integer {sum}",
        };
    }

    /// sin / cos / tan of a Q15.16 angle as a float provider. The angle is first reduced by 2π exactly like
    /// __math/phase (in ints), then the game's sine table is read at the entry below x and corrected to first
    /// order: sin(x) ≈ sin(xᵢ) + cos(xᵢ)·(x − xᵢ), |x − xᵢ| < 2π/65536 — well under one Q15.16 step of error.
    string TrigExpr(string fn, Operand x)
    {
        string s = PInt(x);
        string k = P.Bin("floor_div", s, P.Num(TwoPiInt + 1));
        string r = P.Bin("sub", s, P.List("add", P.List("mul", k, P.Num(TwoPiInt)), P.Bin("floor_div", P.List("mul", k, P.Num(TwoPiFrac)), "65536")));
        string xf = P.Bin("div", P.Un("from_int", r), "65536.0");
        const string K = "10430.378";                                   // table entries per radian
        string i = P.Un("floor", P.List("mul", xf, K));
        string t = P.Bin("div", P.List("add", i, "0.5"), K);            // middle of entry i: the table index is exactly i
        string f = P.Bin("sub", xf, P.Bin("div", i, K));
        string sin = P.List("add", P.Un("sin", t), P.List("mul", P.Un("cos", t), f));
        string cos = P.Bin("sub", P.Un("cos", t), P.List("mul", P.Un("sin", t), f));
        return fn switch { "sin" => sin, "cos" => cos, _ => P.Bin("div", sin, cos) };
    }
}
