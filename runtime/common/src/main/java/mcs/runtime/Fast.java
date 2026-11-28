package mcs.runtime;

import net.minecraft.nbt.CompoundTag;
import net.minecraft.nbt.Tag;
import net.minecraft.resources.Identifier;
import net.minecraft.server.MinecraftServer;
import net.minecraft.world.scores.Objective;
import net.minecraft.world.scores.ScoreHolder;
import net.minecraft.world.scores.Scoreboard;

/**
 * `mcs fast <op> …`: the MC# compiler's shared helpers (<ns>:__math/*, <ns>:__str/indexof) in one step.
 * Each one follows its mcfunction version operation by operation (scoreboard / is floor division, % is floor
 * modulo, dividing by zero leaves the score unchanged, ints wrap), so results are bit-identical.
 * Registers: "$__m.<name>" in the pack's objective (inputs a, b, x; results a or r).
 */
final class Fast {
    private Fast() {}

    // Q15.16 constants (Codegen.Math.cs)
    private static final int TWO_PI_INT = 411774, TWO_PI_FRAC = 54545, RAD_TO_STEP = 10680707;
    private static final int LUT_SIZE = 256, QUARTER = LUT_SIZE * 65536;
    private static final int[] PACKED = new int[LUT_SIZE + 1];

    static {
        int[] table = new int[LUT_SIZE + 1];
        // Math.rint rounds half to even, like C#'s Math.Round (no entry is near a .5 boundary anyway)
        for (int k = 0; k <= LUT_SIZE; k++) table[k] = (int) Math.rint(Math.sin(k * Math.PI / (2 * LUT_SIZE)) * 65536);
        for (int k = 0; k <= LUT_SIZE; k++) PACKED[k] = table[k] * 1024 + (k < LUT_SIZE ? table[k + 1] - table[k] : 0);
    }

    /** scoreboard players operation a /= b */
    private static int div(int a, int b) { return b == 0 ? a : Math.floorDiv(a, b); }
    /** scoreboard players operation a %= b */
    private static int mod(int a, int b) { return b == 0 ? a : Math.floorMod(a, b); }

    /** The registers of one call. */
    private static final class Regs {
        final Scoreboard sb;
        final Objective obj;

        Regs(MinecraftServer s, String objective) {
            sb = s.getScoreboard();
            obj = sb.getObjective(objective);
            if (obj == null) throw new IllegalArgumentException("Unknown objective " + objective);
        }

        int get(String r) {
            var info = sb.getPlayerScoreInfo(ScoreHolder.forNameOnly("$__m." + r), obj);
            return info == null ? 0 : info.value();
        }

        void set(String r, int v) { sb.getOrCreatePlayerScore(ScoreHolder.forNameOnly("$__m." + r), obj).set(v); }
    }

    /** Runs one op; returns the command result. */
    static int run(MinecraftServer s, String op, String[] args) {
        if (op.equals("indexof")) return indexOf(s, args[0], args[1]);
        Regs r = new Regs(s, args[0]);
        switch (op) {
            case "dmul" -> r.set("a", dmul(r.get("a"), r.get("b")));
            case "mmul" -> r.set("a", mmul(r.get("a"), r.get("b")));
            case "mdiv" -> r.set("a", mdiv(r.get("a"), r.get("b")));
            case "ddiv" -> r.set("a", ddiv(r.get("a"), r.get("b")));
            case "idiv" -> r.set("a", idiv(r.get("a"), r.get("b"), true));
            case "irem" -> r.set("a", idiv(r.get("a"), r.get("b"), false));
            case "sin" -> r.set("r", sinp(phase(r.get("x"))));
            case "cos" -> r.set("r", sinp(phase(r.get("x")) + QUARTER));
            case "tan" -> {
                int t = phase(r.get("x"));
                r.set("r", ddiv(sinp(t), sinp(t + QUARTER)));
            }
            default -> throw new IllegalArgumentException("Unknown fast op " + op);
        }
        return 1;
    }

    /** floor(a * b / 65536) (dmul) */
    static int dmul(int a, int b) {
        int ah = div(a, 65536), al = mod(a, 65536), bh = div(b, 65536), bl = mod(b, 65536);
        int a1 = div(al, 256), a0 = mod(al, 256);
        a1 *= bl;
        a0 *= bl;
        a0 = div(a0, 256);
        a1 += a0;
        a1 = div(a1, 256);
        int res = ah * bh * 65536;
        res += ah * bl;
        res += al * bh;
        res += a1;
        return res;
    }

    /** floor(a * b / 1000) (mmul) */
    static int mmul(int a, int b) {
        final int sc = 1000;
        int ah = div(a, sc), al = mod(a, sc), bh = div(b, sc), bl = mod(b, sc);
        int res = ah * bh * sc;
        res += ah * bl;
        res += al * bh;
        res += div(al * bl, sc);
        return res;
    }

    /** a * 1000 / b toward zero (mdiv) */
    static int mdiv(int a, int b) {
        int neg = 0;
        if (a <= -1) { neg++; a *= -1; }
        if (b <= -1) { neg++; b *= -1; }
        if (b != 0) {
            int q = div(a, b), r = mod(a, b);
            if (b <= 2147483) { r *= 1000; r = div(r, b); }
            if (b >= 2147484) { int d = div(b, 1000); r = div(r, d); }
            if (r >= 1000) r = 999;
            a = q * 1000 + r;
        }
        if (neg == 1) a *= -1;
        return a;
    }

    /** a * 65536 / b toward zero, saturating (ddiv) */
    static int ddiv(int a, int b) {
        int neg = 0;
        if (a <= -1) { neg++; a *= -1; }
        if (b <= -1) { neg++; b *= -1; }
        if (b == 0 && a >= 1) a = Integer.MAX_VALUE;
        if (b != 0) {
            int q = div(a, b), r = mod(a, b);
            if (q >= 32768) a = Integer.MAX_VALUE;
            if (q <= 32767) {
                int f = 0;
                for (int i = 0; i < 16; i++) {
                    f += f;
                    int d = b - r;
                    int c = r >= d ? 1 : 0;
                    f += c;
                    if (c == 1) r -= d; else r += r;
                }
                a = q * 65536 + f;
            }
        }
        if (neg == 1) a *= -1;
        return a;
    }

    /** C# a / b (quotient) or a % b, b == 0 leaves a (idiv / irem) */
    static int idiv(int a, int b, boolean quotient) {
        int neg = 0;
        if (a <= -1) { neg++; a *= -1; }
        if (quotient && b <= -1) neg++;
        if (b <= -1) b *= -1;
        if (b != 0) a = quotient ? div(a, b) : mod(a, b);
        if (neg == 1) a *= -1;
        return a;
    }

    /** radians (Q15.16) → table phase (phase) */
    static int phase(int x) {
        int k = div(x, TWO_PI_INT + 1);
        x -= k * TWO_PI_INT;
        k *= TWO_PI_FRAC;
        k = div(k, 65536);
        x -= k;
        return dmul(x, RAD_TO_STEP);
    }

    /** phase → sin (Q15.16) (sinp) */
    static int sinp(int a) {
        a = mod(a, 4 * QUARTER);
        int q = div(a, QUARTER), s = mod(a, QUARTER);
        if (mod(q, 2) == 1) { s *= -1; s += QUARTER; }
        int i = div(s, 65536), fr = mod(s, 65536);
        int p = PACKED[i];
        int r = div(p, 1024);
        p = mod(p, 1024);
        p *= fr;
        p = div(p, 65536);
        r += p;
        if (q >= 2) r *= -1;
        return r;
    }

    /** str.h / str.n in the storage → $__str.r = index of the first occurrence or -1 (__str/indexof) */
    static int indexOf(MinecraftServer s, String storage, String objective) {
        CompoundTag root = s.getCommandStorage().get(Identifier.parse(storage));
        CompoundTag str = root.getCompoundOrEmpty("str");
        String h = str(str.get("h")), n = str(str.get("n"));
        int r = h == null || n == null ? -1 : h.indexOf(n);
        Scoreboard sb = s.getScoreboard();
        Objective obj = sb.getObjective(objective);
        if (obj == null) throw new IllegalArgumentException("Unknown objective " + objective);
        sb.getOrCreatePlayerScore(ScoreHolder.forNameOnly("$__str.r"), obj).set(r);
        return 1;
    }

    private static String str(Tag t) {
        return t == null ? null : t.asString().orElse(null);
    }
}
