package mcs.runtime.interp;

import mcs.runtime.Debugger;
import mcs.runtime.McsRuntime;
import mcs.runtime.Reflect;
import mcs.runtime.Symbols;
import mcs.runtime.interp.Parser.*;
import net.minecraft.commands.CommandSourceStack;
import net.minecraft.commands.functions.CommandFunction;
import net.minecraft.resources.Identifier;
import net.minecraft.server.MinecraftServer;

import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.Deque;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.Optional;
import java.util.concurrent.ThreadLocalRandom;
import java.util.function.Consumer;

/**
 * Runs parsed MC# statements. Locals live in Java; names that are not locals refer to the linked datapack:
 * its static fields (read and written as scores / storage) and its methods (called with the compiler's calling
 * convention: parameter scores, then the function, then the return score).
 */
final class Interpreter {
    static final class Error extends RuntimeException {
        final int line;
        Error(String msg, int line) { super(msg); this.line = line; }
    }

    /** A local variable: its declared type (int, bool, q16, milli, string, var …) and value. */
    static final class Local {
        final String type;
        Object value;
        Local(String type, Object value) { this.type = type; this.value = value; }
    }

    private static final class BreakSignal extends RuntimeException { BreakSignal() { super(null, null, false, false); } }
    private static final class ContinueSignal extends RuntimeException { ContinueSignal() { super(null, null, false, false); } }

    private final MinecraftServer server;
    private final CommandSourceStack source;
    private final Symbols.Pack pack;
    private final Deque<Map<String, Local>> scopes = new ArrayDeque<>();
    private final Consumer<String> out;
    private final int maxSteps;
    private int steps;

    Interpreter(MinecraftServer server, CommandSourceStack source, Symbols.Pack pack, Map<String, Local> globals, Consumer<String> out, int maxSteps) {
        this.server = server;
        this.source = source;
        this.pack = pack;
        this.out = out;
        this.maxSteps = maxSteps;
        scopes.push(globals);
    }

    /** Runs a program; an expression statement at the top level prints its value (like a REPL). */
    void run(List<Stmt> program) {
        for (Stmt s : program) {
            if (s instanceof ExprStmt es && echoes(es.e())) {
                Object v = eval(es.e());
                if (v != null) out.accept("= " + Symbols.format(v));
            } else exec(s);
        }
    }

    private static boolean echoes(Expr e) {
        return !(e instanceof Assign || e instanceof IncDec);
    }

    // ---------------------------------------------------------------- statements

    private void exec(Stmt s) {
        switch (s) {
            case ExprStmt es -> eval(es.e());
            case Decl d -> {
                for (int i = 0; i < d.names().size(); i++) {
                    String name = d.names().get(i);
                    Expr init = d.inits().get(i);
                    Object v = init == null ? defaultOf(d.type()) : eval(init);
                    if (d.type().equals("var") && v == null) throw new Error("'var " + name + "' needs a value", d.line());
                    String type = d.type().equals("var") ? typeOf(v) : d.type();
                    scopes.peek().put(name, new Local(type, convert(type, v, d.line())));
                }
            }
            case Block b -> {
                scopes.push(new HashMap<>());
                try { for (Stmt x : b.body()) exec(x); } finally { scopes.pop(); }
            }
            case If i -> {
                if (truthy(eval(i.c()))) exec(i.then());
                else if (i.otherwise() != null) exec(i.otherwise());
            }
            case While w -> {
                while (truthy(eval(w.c()))) {
                    step();
                    try { exec(w.body()); } catch (BreakSignal e) { break; } catch (ContinueSignal ignored) {}
                }
            }
            case DoWhile w -> {
                do {
                    step();
                    try { exec(w.body()); } catch (BreakSignal e) { break; } catch (ContinueSignal ignored) {}
                } while (truthy(eval(w.c())));
            }
            case For f -> {
                scopes.push(new HashMap<>());
                try {
                    for (Stmt x : f.init()) exec(x);
                    while (f.c() == null || truthy(eval(f.c()))) {
                        step();
                        try { exec(f.body()); } catch (BreakSignal e) { break; } catch (ContinueSignal ignored) {}
                        for (Expr x : f.step()) eval(x);
                    }
                } finally { scopes.pop(); }
            }
            case Break b -> throw new BreakSignal();
            case Continue c -> throw new ContinueSignal();
            case Command c -> {
                int r = command(c.text());
                out.accept("/" + c.text() + "  → " + r);
            }
        }
    }

    private void step() {
        if (++steps > maxSteps) throw new Error("stopped after " + maxSteps + " loop iterations (interpreter.max-steps)", 0);
    }

    // ---------------------------------------------------------------- expressions

    Object eval(Expr e) {
        return switch (e) {
            case Lit l -> l.value();
            case Interp in -> {
                StringBuilder sb = new StringBuilder();
                for (Object part : in.parts()) sb.append(part instanceof Expr x ? Symbols.format(eval(x)) : (String) part);
                yield sb.toString();
            }
            case Name n -> read(n.name(), n.line());
            case Call c -> call(c);
            case Unary u -> {
                Object v = eval(u.e());
                yield switch (u.op()) {
                    case "!" -> !truthy(v);
                    case "-" -> v instanceof Integer i ? (Object) (-i) : (Object) (-num(v, 0));
                    default -> v;
                };
            }
            case Binary b -> binary(b);
            case Assign a -> {
                Object v = eval(a.value());
                if (!a.op().equals("=")) v = arith(a.op().substring(0, 1), read(a.target(), a.line()), v, a.line());
                yield write(a.target(), v, a.line());
            }
            case IncDec d -> {
                Object old = read(d.target(), d.line());
                Object now = arith(d.op().equals("++") ? "+" : "-", old, 1, d.line());
                now = write(d.target(), now, d.line());
                yield d.prefix() ? now : old;
            }
            case Cond c -> truthy(eval(c.c())) ? eval(c.a()) : eval(c.b());
            case Cast c -> convert(normType(c.type()), eval(c.e()), 0);
        };
    }

    private Object binary(Binary b) {
        String op = b.op();
        if (op.equals("&&")) return truthy(eval(b.l())) && truthy(eval(b.r()));
        if (op.equals("||")) return truthy(eval(b.l())) || truthy(eval(b.r()));
        Object l = eval(b.l()), r = eval(b.r());
        switch (op) {
            case "==": return equal(l, r);
            case "!=": return !equal(l, r);
            case "<": return num(l, b.line()) < num(r, b.line());
            case ">": return num(l, b.line()) > num(r, b.line());
            case "<=": return num(l, b.line()) <= num(r, b.line());
            case ">=": return num(l, b.line()) >= num(r, b.line());
            default: return arith(op, l, r, b.line());
        }
    }

    private static boolean equal(Object l, Object r) {
        if (l instanceof Number a && r instanceof Number b) return a.doubleValue() == b.doubleValue();
        return l == null ? r == null : l.equals(r);
    }

    /** + - * / % with C# semantics: ints wrap, / and % truncate toward zero, decimals otherwise. */
    private static Object arith(String op, Object l, Object r, int line) {
        if (op.equals("+") && (l instanceof String || r instanceof String)) return Symbols.format(l) + Symbols.format(r);
        if (l instanceof Integer a && r instanceof Integer b) {
            return switch (op) {
                case "+" -> a + b;
                case "-" -> a - b;
                case "*" -> a * b;
                case "/" -> { if (b == 0) throw new Error("division by zero", line); yield a / b; }
                case "%" -> { if (b == 0) throw new Error("division by zero", line); yield a % b; }
                default -> throw new Error("unknown operator " + op, line);
            };
        }
        double a = num(l, line), b = num(r, line);
        return switch (op) {
            case "+" -> a + b;
            case "-" -> a - b;
            case "*" -> a * b;
            case "/" -> { if (b == 0) throw new Error("division by zero", line); yield a / b; }
            case "%" -> a % b;
            default -> throw new Error("unknown operator " + op, line);
        };
    }

    private static double num(Object v, int line) {
        if (v instanceof Number n) return n.doubleValue();
        throw new Error("expected a number, got " + describe(v), line);
    }

    private static boolean truthy(Object v) {
        if (v instanceof Boolean b) return b;
        throw new Error("expected a bool, got " + describe(v), 0);
    }

    private static String describe(Object v) {
        return v == null ? "nothing" : v instanceof String s ? "\"" + s + "\"" : Symbols.format(v);
    }

    // ---------------------------------------------------------------- types

    private static String normType(String t) {
        return switch (t) {
            case "Q16", "double" -> "q16";
            case "Milli", "float" -> "milli";
            case "string", "NString" -> "string";
            case "long" -> "int";
            default -> t;
        };
    }

    private static String typeOf(Object v) {
        return v instanceof Integer ? "int" : v instanceof Double ? "q16" : v instanceof Boolean ? "bool" : "string";
    }

    private static Object defaultOf(String type) {
        return switch (normType(type)) {
            case "int" -> 0;
            case "bool" -> false;
            case "q16", "milli" -> 0.0;
            case "string" -> "";
            default -> null;
        };
    }

    /** A value stored into a variable of this type (decimals are rounded to the type's precision). */
    static Object convert(String type, Object v, int line) {
        String t = normType(type);
        try {
            return switch (t) {
                case "int" -> v instanceof Integer ? v : (Object) (int) num(v, line);
                case "bool" -> truthy(v);
                case "q16" -> Math.round(num(v, line) * 65536) / 65536.0;
                case "milli" -> Math.round(num(v, line) * 1000) / 1000.0;
                case "string" -> v instanceof String ? v : Symbols.format(v);
                default -> v;
            };
        } catch (Error e) {
            throw new Error("cannot store " + describe(v) + " in a " + type, line);
        }
    }

    // ---------------------------------------------------------------- variables

    private Local local(String name) {
        for (Map<String, Local> s : scopes) {
            Local l = s.get(name);
            if (l != null) return l;
        }
        return null;
    }

    private Symbols.Var packVar(String name) {
        return pack == null ? null : Debugger.resolve(pack, name);
    }

    private Object read(String name, int line) {
        Local l = local(name);
        if (l != null) return l.value;
        Symbols.Var v = packVar(name);
        if (v != null) {
            Object val = Symbols.read(server, v);
            return val != null ? val : defaultOf(v.type());
        }
        Object c = constant(name);
        if (c != null) return c;
        throw new Error("unknown name '" + name + "'" + (pack == null ? " (no datapack is linked: /mcsi use <pack>)" : ""), line);
    }

    private Object write(String name, Object value, int line) {
        Local l = local(name);
        if (l != null) return l.value = convert(l.type, value, line);
        Symbols.Var v = packVar(name);
        if (v != null) {
            Object conv = convert(v.type(), value, line);
            Symbols.write(server, v, conv);
            return conv;
        }
        throw new Error("unknown variable '" + name + "' (declare it first: int " + name + " = …;)", line);
    }

    private static Object constant(String name) {
        return switch (name) {
            case "Math.PI", "MathLib.Math.PI" -> Math.PI;
            case "Math.Tau", "MathLib.Math.Tau" -> 2 * Math.PI;
            case "Math.Deg2Rad" -> Math.PI / 180;
            case "Math.Rad2Deg" -> 180 / Math.PI;
            case "int.MaxValue" -> Integer.MAX_VALUE;
            case "int.MinValue" -> Integer.MIN_VALUE;
            default -> null;
        };
    }

    // ---------------------------------------------------------------- calls

    private Object call(Call c) {
        List<Object> a = new ArrayList<>();
        for (Expr x : c.args()) a.add(eval(x));
        int line = c.line();
        switch (c.name()) {
            case "print", "Console.WriteLine", "m.Console.WriteLine", "Tui.Print", "Debug.Log" -> {
                StringBuilder sb = new StringBuilder();
                for (Object o : a) sb.append(Symbols.format(o));
                out.accept(sb.toString());
                return null;
            }
            case "cmd", "run", "m.Result" -> { return command(str(a, 0, line)); }
            case "m.Success" -> { return command(str(a, 0, line)) > 0; }
            case "m.Random", "Random" -> {
                int lo = (int) num(a.get(0), line), hi = (int) num(a.get(1), line);
                return ThreadLocalRandom.current().nextInt(Math.min(lo, hi), Math.max(lo, hi) + 1);
            }
            case "Wait" -> throw new Error("Wait() is not supported by the interpreter", line);
        }
        String m = c.name().startsWith("MathLib.") ? c.name().substring(8) : c.name();
        if (m.startsWith("Math.")) return math(m.substring(5), a, line);

        // a method of the linked datapack
        if (pack == null) throw new Error("unknown function '" + c.name() + "' (no datapack is linked: /mcsi use <pack>)", line);
        Symbols.Fn fn = null;
        for (Symbols.Fn f : pack.functions())
            if (f.name().equals(c.name()) && f.params().size() == a.size()) { fn = f; break; }
        if (fn == null) throw new Error("no method " + c.name() + " with " + a.size() + " argument(s) in " + pack.namespace(), line);
        if (!fn.callable()) throw new Error(c.name() + " cannot be called from the interpreter (it takes or returns objects, NString or NArray, or is an instance method)", line);
        for (int i = 0; i < a.size(); i++) {
            Symbols.Var p = fn.params().get(i);
            Symbols.setScore(server, p.holder(), p.objective(), Symbols.toScore(p.type(), convert(p.type(), a.get(i), line)));
        }
        Optional<CommandFunction<CommandSourceStack>> f = server.getFunctions().get(Identifier.parse(fn.path()));
        if (f.isEmpty()) throw new Error("the function " + fn.path() + " is not loaded", line);
        Reflect.outsideCommands(() -> server.getFunctions().execute(f.get(), source));
        if (fn.returns() == null) return null;
        Object r = Symbols.read(server, fn.returns());
        return r != null ? r : defaultOf(fn.returns().type());
    }

    private static String str(List<Object> a, int i, int line) {
        if (a.size() <= i || !(a.get(i) instanceof String s)) throw new Error("expected a string argument", line);
        return s;
    }

    private static Object math(String name, List<Object> a, int line) {
        double x = a.isEmpty() ? 0 : num(a.get(0), line);
        boolean ints = a.stream().allMatch(v -> v instanceof Integer);
        return switch (name) {
            case "Abs" -> ints ? (Object) Math.abs((int) x) : (Object) Math.abs(x);
            case "Min" -> ints ? (Object) Math.min((int) x, (int) num(a.get(1), line)) : (Object) Math.min(x, num(a.get(1), line));
            case "Max" -> ints ? (Object) Math.max((int) x, (int) num(a.get(1), line)) : (Object) Math.max(x, num(a.get(1), line));
            case "Clamp" -> ints ? (Object) Math.clamp((long) x, (int) num(a.get(1), line), (int) num(a.get(2), line)) : (Object) Math.clamp(x, num(a.get(1), line), num(a.get(2), line));
            case "Sqrt" -> Math.sqrt(x);
            case "Pow" -> Math.pow(x, num(a.get(1), line));
            case "Floor" -> Math.floor(x);
            case "Ceiling" -> Math.ceil(x);
            case "Round" -> (double) Math.round(x);
            case "Sin" -> Math.sin(x);
            case "Cos" -> Math.cos(x);
            case "Tan" -> Math.tan(x);
            case "Atan2" -> Math.atan2(x, num(a.get(1), line));
            case "FloorDiv" -> Math.floorDiv((int) x, (int) num(a.get(1), line));
            case "FloorMod" -> Math.floorMod((int) x, (int) num(a.get(1), line));
            default -> throw new Error("unknown function Math." + name, line);
        };
    }

    /** Runs a Minecraft command as the user (with their own permissions); returns its result. */
    private int command(String cmd) {
        int[] result = { 0 };
        CommandSourceStack src = source.withCallback((ok, r) -> result[0] = ok ? r : 0);
        Reflect.outsideCommands(() -> server.getCommands().performPrefixedCommand(src, cmd));
        return result[0];
    }

    static void log(String msg) { McsRuntime.LOG.debug(msg); }
}
