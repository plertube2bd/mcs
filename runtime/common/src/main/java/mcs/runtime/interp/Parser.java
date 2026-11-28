package mcs.runtime.interp;

import java.util.ArrayList;
import java.util.List;
import java.util.Set;

/** Lexer and recursive-descent parser for the MC# subset the interpreter runs. */
final class Parser {
    // ---------------------------------------------------------------- AST
    sealed interface Expr permits Lit, Interp, Name, Call, Unary, Binary, Assign, IncDec, Cond, Cast {}
    record Lit(Object value) implements Expr {}
    record Interp(List<Object> parts) implements Expr {}                 // String or Expr
    record Name(String name, int line) implements Expr {}                // a or A.b.c
    record Call(String name, List<Expr> args, int line) implements Expr {}
    record Unary(String op, Expr e) implements Expr {}
    record Binary(String op, Expr l, Expr r, int line) implements Expr {}
    record Assign(String op, String target, Expr value, int line) implements Expr {}
    record IncDec(String target, String op, boolean prefix, int line) implements Expr {}
    record Cond(Expr c, Expr a, Expr b) implements Expr {}
    record Cast(String type, Expr e) implements Expr {}

    sealed interface Stmt permits ExprStmt, Decl, Block, If, While, DoWhile, For, Break, Continue, Command {}
    record ExprStmt(Expr e) implements Stmt {}
    record Decl(String type, List<String> names, List<Expr> inits, int line) implements Stmt {}
    record Block(List<Stmt> body) implements Stmt {}
    record If(Expr c, Stmt then, Stmt otherwise) implements Stmt {}
    record While(Expr c, Stmt body) implements Stmt {}
    record DoWhile(Stmt body, Expr c) implements Stmt {}
    record For(List<Stmt> init, Expr c, List<Expr> step, Stmt body) implements Stmt {}
    record Break() implements Stmt {}
    record Continue() implements Stmt {}
    record Command(String text, int line) implements Stmt {}

    static final Set<String> TYPES = Set.of("int", "bool", "Q16", "Milli", "double", "float", "string", "NString", "var", "long");

    static final class Error extends RuntimeException {
        final int line;
        Error(String msg, int line) { super(msg); this.line = line; }
    }

    // ---------------------------------------------------------------- lexer
    enum K { NUM, STR, ISTR, ID, OP, CMD, EOF }
    record Tok(K k, String text, Object value, int line) {}

    private static final String[] OPS = { "==", "!=", "<=", ">=", "&&", "||", "++", "--", "+=", "-=", "*=", "/=", "%=",
        "+", "-", "*", "/", "%", "<", ">", "=", "!", "(", ")", "{", "}", "[", "]", ";", ",", ".", "?", ":" };

    static List<Tok> lex(String src) {
        List<Tok> out = new ArrayList<>();
        int i = 0, line = 1, n = src.length();
        while (i < n) {
            char c = src.charAt(i);
            if (c == '\n') { line++; i++; continue; }
            if (Character.isWhitespace(c)) { i++; continue; }
            if (src.startsWith("//", i)) { while (i < n && src.charAt(i) != '\n') i++; continue; }
            if (src.startsWith("/*", i)) {
                int e = src.indexOf("*/", i + 2);
                if (e < 0) throw new Error("unterminated comment", line);
                for (int k = i; k < e; k++) if (src.charAt(k) == '\n') line++;
                i = e + 2;
                continue;
            }
            // a statement starting with "/" is a Minecraft command (up to the end of the line)
            if (c == '/' && atStatementStart(out)) {
                int e = src.indexOf('\n', i);
                if (e < 0) e = n;
                String cmd = src.substring(i + 1, e).trim();
                if (cmd.endsWith(";")) cmd = cmd.substring(0, cmd.length() - 1).trim();
                out.add(new Tok(K.CMD, cmd, null, line));
                i = e;
                continue;
            }
            if (Character.isDigit(c) || (c == '.' && i + 1 < n && Character.isDigit(src.charAt(i + 1)))) {
                int s = i;
                while (i < n && (Character.isDigit(src.charAt(i)) || src.charAt(i) == '_')) i++;
                boolean dec = false;
                if (i < n && src.charAt(i) == '.' && i + 1 < n && Character.isDigit(src.charAt(i + 1))) {
                    dec = true;
                    i++;
                    while (i < n && Character.isDigit(src.charAt(i))) i++;
                }
                String t = src.substring(s, i).replace("_", "");
                if (i < n && "fFdDmM".indexOf(src.charAt(i)) >= 0) { dec = true; i++; }
                try {
                    out.add(new Tok(K.NUM, t, dec ? (Object) Double.parseDouble(t) : (Object) Integer.parseInt(t), line));
                } catch (NumberFormatException e) {
                    throw new Error("number out of range: " + t, line);
                }
                continue;
            }
            if (c == '"' || (c == '$' && i + 1 < n && src.charAt(i + 1) == '"')) {
                boolean interp = c == '$';
                i += interp ? 2 : 1;
                StringBuilder sb = new StringBuilder();
                int depth = 0;
                while (true) {
                    if (i >= n) throw new Error("unterminated string", line);
                    char d = src.charAt(i);
                    if (d == '"' && depth == 0) { i++; break; }
                    if (d == '\\' && i + 1 < n) {
                        char e = src.charAt(i + 1);
                        if (interp && depth > 0) sb.append(d).append(e);
                        else sb.append(switch (e) { case 'n' -> '\n'; case 't' -> '\t'; default -> e; });
                        i += 2;
                        continue;
                    }
                    if (interp) {
                        if (d == '{' && depth == 0 && i + 1 < n && src.charAt(i + 1) == '{') { sb.append("{{"); i += 2; continue; }
                        if (d == '}' && depth == 0 && i + 1 < n && src.charAt(i + 1) == '}') { sb.append("}}"); i += 2; continue; }
                        if (d == '{') depth++;
                        if (d == '}') depth--;
                    }
                    if (d == '\n') line++;
                    sb.append(d);
                    i++;
                }
                out.add(new Tok(interp ? K.ISTR : K.STR, sb.toString(), sb.toString(), line));
                continue;
            }
            if (Character.isLetter(c) || c == '_') {
                int s = i;
                while (i < n && (Character.isLetterOrDigit(src.charAt(i)) || src.charAt(i) == '_')) i++;
                out.add(new Tok(K.ID, src.substring(s, i), null, line));
                continue;
            }
            String op = null;
            for (String o : OPS) if (src.startsWith(o, i)) { op = o; break; }
            if (op == null) throw new Error("unexpected character '" + c + "'", line);
            out.add(new Tok(K.OP, op, null, line));
            i += op.length();
        }
        out.add(new Tok(K.EOF, "", null, line));
        return out;
    }

    private static boolean atStatementStart(List<Tok> out) {
        if (out.isEmpty()) return true;
        Tok t = out.get(out.size() - 1);
        return t.k == K.CMD || t.k == K.OP && (t.text.equals(";") || t.text.equals("{") || t.text.equals("}"));
    }

    // ---------------------------------------------------------------- parser
    private final List<Tok> toks;
    private int p;

    private Parser(List<Tok> toks) { this.toks = toks; }

    static List<Stmt> parseProgram(String src) {
        Parser ps = new Parser(lex(src));
        List<Stmt> out = new ArrayList<>();
        while (ps.peek().k != K.EOF) out.add(ps.statement(true));
        return out;
    }

    private Tok peek() { return toks.get(p); }
    private Tok peek(int k) { return toks.get(Math.min(p + k, toks.size() - 1)); }
    private Tok next() { return toks.get(p++); }
    private boolean is(String op) { Tok t = peek(); return (t.k == K.OP || t.k == K.ID) && t.text.equals(op); }
    private boolean eat(String op) { if (is(op)) { p++; return true; } return false; }

    private Tok expect(String op) {
        if (!is(op)) throw new Error("expected '" + op + "' but found " + describe(peek()), peek().line);
        return next();
    }

    private static String describe(Tok t) { return t.k == K.EOF ? "the end of the input" : "'" + t.text + "'"; }

    /** The ";" after a simple statement (optional at the very end, as in a REPL). */
    private void endStatement() {
        if (eat(";")) return;
        if (peek().k == K.EOF || is("}")) return;
        throw new Error("expected ';' but found " + describe(peek()), peek().line);
    }

    private Stmt statement(boolean top) {
        Tok t = peek();
        if (t.k == K.CMD) { next(); eat(";"); return new Command(t.text, t.line); }
        if (eat("{")) {
            List<Stmt> body = new ArrayList<>();
            while (!is("}")) {
                if (peek().k == K.EOF) throw new Error("missing '}'", peek().line);
                body.add(statement(false));
            }
            expect("}");
            return new Block(body);
        }
        if (eat(";")) return new Block(List.of());
        if (eat("if")) {
            expect("(");
            Expr c = expr();
            expect(")");
            Stmt a = statement(false);
            Stmt b = eat("else") ? statement(false) : null;
            return new If(c, a, b);
        }
        if (eat("while")) { expect("("); Expr c = expr(); expect(")"); return new While(c, statement(false)); }
        if (eat("do")) {
            Stmt body = statement(false);
            expect("while"); expect("("); Expr c = expr(); expect(")"); endStatement();
            return new DoWhile(body, c);
        }
        if (eat("for")) {
            expect("(");
            List<Stmt> init = new ArrayList<>();
            if (!is(";")) {
                if (isDecl()) init.add(declaration());
                else { init.add(new ExprStmt(expr())); while (eat(",")) init.add(new ExprStmt(expr())); }
            }
            expect(";");
            Expr c = is(";") ? null : expr();
            expect(";");
            List<Expr> step = new ArrayList<>();
            if (!is(")")) { step.add(expr()); while (eat(",")) step.add(expr()); }
            expect(")");
            return new For(init, c, step, statement(false));
        }
        if (eat("break")) { endStatement(); return new Break(); }
        if (eat("continue")) { endStatement(); return new Continue(); }
        if (is("foreach") || is("switch") || is("return") || is("class") || is("static") || is("public"))
            throw new Error("'" + t.text + "' is not supported by the interpreter", t.line);
        if (isDecl()) { Stmt d = declaration(); endStatement(); return d; }
        Expr e = expr();
        endStatement();
        return new ExprStmt(e);
    }

    private boolean isDecl() {
        Tok t = peek();
        return t.k == K.ID && TYPES.contains(t.text) && peek(1).k == K.ID;
    }

    private Decl declaration() {
        Tok type = next();
        List<String> names = new ArrayList<>();
        List<Expr> inits = new ArrayList<>();
        do {
            Tok name = next();
            if (name.k != K.ID) throw new Error("expected a variable name", name.line);
            names.add(name.text);
            inits.add(eat("=") ? expr() : null);
        } while (eat(","));
        return new Decl(type.text, names, inits, type.line);
    }

    // expressions, lowest precedence first
    Expr expr() { return assignment(); }

    private Expr assignment() {
        Expr l = ternary();
        Tok t = peek();
        if (t.k == K.OP && Set.of("=", "+=", "-=", "*=", "/=", "%=").contains(t.text)) {
            if (!(l instanceof Name n)) throw new Error("only variables can be assigned", t.line);
            next();
            return new Assign(t.text, n.name(), assignment(), t.line);
        }
        return l;
    }

    private Expr ternary() {
        Expr c = binary(0);
        if (eat("?")) {
            Expr a = expr();
            expect(":");
            return new Cond(c, a, ternary());
        }
        return c;
    }

    private static final String[][] LEVELS = { { "||" }, { "&&" }, { "==", "!=" }, { "<", ">", "<=", ">=" }, { "+", "-" }, { "*", "/", "%" } };

    private Expr binary(int level) {
        if (level == LEVELS.length) return unary();
        Expr l = binary(level + 1);
        while (true) {
            Tok t = peek();
            String found = null;
            if (t.k == K.OP) for (String o : LEVELS[level]) if (o.equals(t.text)) found = o;
            if (found == null) return l;
            next();
            l = new Binary(found, l, binary(level + 1), t.line);
        }
    }

    private Expr unary() {
        Tok t = peek();
        if (t.k == K.OP && (t.text.equals("!") || t.text.equals("-") || t.text.equals("+"))) { next(); return new Unary(t.text, unary()); }
        if (t.k == K.OP && (t.text.equals("++") || t.text.equals("--"))) {
            next();
            Expr e = unary();
            if (!(e instanceof Name n)) throw new Error(t.text + " needs a variable", t.line);
            return new IncDec(n.name(), t.text, true, t.line);
        }
        // a cast: (int)x
        if (is("(") && peek(1).k == K.ID && TYPES.contains(peek(1).text) && peek(2).k == K.OP && peek(2).text.equals(")")) {
            next();
            String type = next().text;
            next();
            return new Cast(type, unary());
        }
        return postfix(primary());
    }

    private Expr postfix(Expr e) {
        Tok t = peek();
        if (t.k == K.OP && (t.text.equals("++") || t.text.equals("--"))) {
            if (!(e instanceof Name n)) throw new Error(t.text + " needs a variable", t.line);
            next();
            return new IncDec(n.name(), t.text, false, t.line);
        }
        return e;
    }

    private Expr primary() {
        Tok t = next();
        switch (t.k) {
            case NUM: return new Lit(t.value);
            case STR: return new Lit(t.value);
            case ISTR: return interpolated(t);
            case ID: {
                if (t.text.equals("true")) return new Lit(true);
                if (t.text.equals("false")) return new Lit(false);
                if (t.text.equals("new")) throw new Error("'new' is not supported by the interpreter", t.line);
                StringBuilder name = new StringBuilder(t.text);
                while (is(".") && peek(1).k == K.ID) { next(); name.append('.').append(next().text); }
                if (eat("(")) {
                    List<Expr> args = new ArrayList<>();
                    if (!is(")")) { args.add(expr()); while (eat(",")) args.add(expr()); }
                    expect(")");
                    return new Call(name.toString(), args, t.line);
                }
                return new Name(name.toString(), t.line);
            }
            case OP:
                if (t.text.equals("(")) { Expr e = expr(); expect(")"); return e; }
                // fall through
            default:
                throw new Error("unexpected " + describe(t), t.line);
        }
    }

    private Expr interpolated(Tok t) {
        String s = t.text;
        List<Object> parts = new ArrayList<>();
        StringBuilder lit = new StringBuilder();
        for (int i = 0; i < s.length(); i++) {
            char c = s.charAt(i);
            if (c == '{' && i + 1 < s.length() && s.charAt(i + 1) == '{') { lit.append('{'); i++; continue; }
            if (c == '}' && i + 1 < s.length() && s.charAt(i + 1) == '}') { lit.append('}'); i++; continue; }
            if (c == '{') {
                int depth = 1, j = i + 1;
                while (j < s.length() && depth > 0) { if (s.charAt(j) == '{') depth++; if (s.charAt(j) == '}') depth--; j++; }
                if (depth != 0) throw new Error("missing '}' in an interpolated string", t.line);
                String inner = s.substring(i + 1, j - 1);
                int colon = topLevelColon(inner);      // {x:0.00} format specifiers are ignored
                if (colon >= 0) inner = inner.substring(0, colon);
                if (lit.length() > 0) { parts.add(lit.toString()); lit.setLength(0); }
                Parser sub = new Parser(lex(inner));
                Expr e = sub.expr();
                if (sub.peek().k != K.EOF) throw new Error("unexpected " + describe(sub.peek()) + " in {" + inner + "}", t.line);
                parts.add(e);
                i = j - 1;
                continue;
            }
            lit.append(c);
        }
        if (lit.length() > 0) parts.add(lit.toString());
        return new Interp(parts);
    }

    private static int topLevelColon(String s) {
        int depth = 0;
        boolean q = false;
        for (int i = 0; i < s.length(); i++) {
            char c = s.charAt(i);
            if (c == '"') q = !q;
            if (q) continue;
            if (c == '(') depth++;
            if (c == ')') depth--;
            if (c == '?') return -1;      // a ternary, not a format
            if (c == ':' && depth == 0) return i;
        }
        return -1;
    }
}
