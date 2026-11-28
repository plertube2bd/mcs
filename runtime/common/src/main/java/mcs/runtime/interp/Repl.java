package mcs.runtime.interp;

import com.mojang.brigadier.CommandDispatcher;
import mcs.runtime.McsRuntime;
import mcs.runtime.Symbols;
import net.minecraft.ChatFormatting;
import net.minecraft.commands.CommandSourceStack;
import net.minecraft.commands.SharedSuggestionProvider;
import net.minecraft.network.chat.Component;
import net.minecraft.server.MinecraftServer;
import net.minecraft.server.level.ServerPlayer;

import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.UUID;
import java.util.concurrent.ConcurrentHashMap;

import static com.mojang.brigadier.arguments.StringArgumentType.getString;
import static com.mojang.brigadier.arguments.StringArgumentType.greedyString;
import static com.mojang.brigadier.arguments.StringArgumentType.word;
import static net.minecraft.commands.Commands.argument;
import static net.minecraft.commands.Commands.literal;

/**
 * /mcsi: the MC# interpreter (operators only).
 *   /mcsi             interactive mode: chat lines are MC# code until "exit"
 *   /mcsi run <code>  runs code once
 *   /mcsi use <pack>  links the session to a datapack (its statics and methods become names)
 *   /mcsi vars | reset
 * Calls into the datapack and commands run outside the current command execution (Reflect.outsideCommands), so
 * they complete before the next statement and their results can be read.
 */
public final class Repl {
    private Repl() {}

    private static final UUID CONSOLE = new UUID(0, 0);

    private static final class Session {
        String pack;
        final Map<String, Interpreter.Local> locals = new HashMap<>();
        boolean interactive;
        final StringBuilder pending = new StringBuilder();
    }

    private static final Map<UUID, Session> sessions = new ConcurrentHashMap<>();

    private static UUID who(CommandSourceStack src) {
        return src.getPlayer() != null ? src.getPlayer().getUUID() : CONSOLE;
    }

    private static Session session(UUID id) {
        return sessions.computeIfAbsent(id, k -> {
            Session s = new Session();
            if (Symbols.packs().size() == 1) s.pack = Symbols.packs().keySet().iterator().next();
            return s;
        });
    }

    public static void register(CommandDispatcher<CommandSourceStack> d) {
        int level = McsRuntime.config().interpreterLevel;
        d.register(literal("mcsi").requires(s -> McsRuntime.hasLevel(s, level))
            .executes(c -> toggle(c.getSource()))
            .then(literal("run").then(argument("code", greedyString()).executes(c -> submit(c.getSource(), getString(c, "code")))))
            .then(literal("use").then(argument("pack", word()).suggests((c, b) -> SharedSuggestionProvider.suggest(Symbols.packs().keySet(), b))
                .executes(c -> use(c.getSource(), getString(c, "pack")))))
            .then(literal("vars").executes(c -> vars(c.getSource())))
            .then(literal("reset").executes(c -> { session(who(c.getSource())).locals.clear(); McsRuntime.info(c.getSource(), "mcsi: variables cleared"); return 1; }))
            .then(literal("exit").executes(c -> exit(c.getSource()))));
    }

    private static Component banner(Session s) {
        return Component.literal("MC# interpreter").withStyle(ChatFormatting.GOLD)
            .append(Component.literal(" — linked to " + (s.pack == null ? "no datapack (/mcsi use <pack>)" : s.pack)
                + ". Chat lines are now MC# code; a line starting with / is a command. \"exit\" to leave.").withStyle(ChatFormatting.GRAY));
    }

    private static int toggle(CommandSourceStack src) {
        if (src.getPlayer() == null) {
            McsRuntime.info(src, "Usage: mcsi run <code>   (interactive mode is for players)");
            return 0;
        }
        Session s = session(who(src));
        if (s.interactive) return exit(src);
        s.interactive = true;
        s.pending.setLength(0);
        McsRuntime.info(src, banner(s));
        return 1;
    }

    private static int exit(CommandSourceStack src) {
        Session s = session(who(src));
        s.interactive = false;
        s.pending.setLength(0);
        McsRuntime.info(src, Component.literal("mcsi: left interactive mode").withStyle(ChatFormatting.GRAY));
        return 1;
    }

    private static int use(CommandSourceStack src, String pack) {
        if (Symbols.pack(pack) == null) {
            McsRuntime.error(src, "mcsi: no MC# datapack '" + pack + "'. Loaded: " + Symbols.packs().keySet());
            return 0;
        }
        session(who(src)).pack = pack;
        McsRuntime.info(src, "mcsi: linked to " + pack);
        return 1;
    }

    private static int vars(CommandSourceStack src) {
        Session s = session(who(src));
        if (s.locals.isEmpty()) McsRuntime.info(src, "mcsi: no variables");
        s.locals.forEach((k, v) -> McsRuntime.info(src, "  " + v.type + " " + k + " = " + Symbols.format(v.value)));
        return s.locals.size();
    }

    private static int submit(CommandSourceStack src, String code) {
        run(src.getServer(), src, session(who(src)), code);
        return 1;
    }

    private static void run(MinecraftServer server, CommandSourceStack src, Session s, String code) {
        Symbols.Pack pack = s.pack == null ? null : Symbols.pack(s.pack);
        try {
            List<Parser.Stmt> program = Parser.parseProgram(code);
            new Interpreter(server, src, pack, s.locals, msg -> src.sendSystemMessage(Component.literal(msg)), McsRuntime.config().interpreterMaxSteps).run(program);
        } catch (Parser.Error e) {
            fail(src, "syntax error" + (e.line > 1 ? " (line " + e.line + ")" : "") + ": " + e.getMessage());
        } catch (Interpreter.Error e) {
            fail(src, (e.line > 1 ? "line " + e.line + ": " : "") + e.getMessage());
        } catch (RuntimeException e) {
            fail(src, e.toString());
            McsRuntime.LOG.warn("mcsi", e);
        }
    }

    private static void fail(CommandSourceStack src, String msg) {
        src.sendSystemMessage(Component.literal("mcsi: " + msg).withStyle(ChatFormatting.RED));
    }

    /** Chat from a player in interactive mode is code. May be called from any thread. */
    public static boolean chat(ServerPlayer player, String message) {
        Session s = sessions.get(player.getUUID());
        if (s == null || !s.interactive) return false;
        MinecraftServer server = McsRuntime.server();
        if (server == null) return false;
        String line = message.strip();
        if (s.pending.isEmpty() && (line.equals("exit") || line.equals(".exit") || line.equals("quit"))) {
            server.execute(() -> exit(player.createCommandSourceStack()));
            return true;
        }
        synchronized (s) {
            s.pending.append(line).append('\n');
            String code = s.pending.toString();
            if (!complete(code)) {
                server.execute(() -> player.sendSystemMessage(Component.literal("... " + line).withStyle(ChatFormatting.DARK_GRAY)));
                return true;
            }
            s.pending.setLength(0);
            server.execute(() -> {
                player.sendSystemMessage(Component.literal("mcsi> " + code.strip().replace("\n", " ")).withStyle(ChatFormatting.GRAY));
                run(server, player.createCommandSourceStack(), s, code);
            });
        }
        return true;
    }

    /** Are the braces / parentheses balanced (outside strings)? Then the input is complete. */
    static boolean complete(String code) {
        int depth = 0;
        boolean str = false;
        for (int i = 0; i < code.length(); i++) {
            char c = code.charAt(i);
            if (c == '\\' && str) { i++; continue; }
            if (c == '"') str = !str;
            if (str) continue;
            if (c == '{' || c == '(') depth++;
            if (c == '}' || c == ')') depth--;
        }
        return depth <= 0 && !str;
    }

    public static void leave(UUID player) {
        sessions.remove(player);
    }
}
