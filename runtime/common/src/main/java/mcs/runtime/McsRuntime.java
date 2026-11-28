package mcs.runtime;

import com.mojang.brigadier.CommandDispatcher;
import mcs.runtime.interp.Repl;
import net.minecraft.ChatFormatting;
import net.minecraft.commands.CommandSourceStack;
import net.minecraft.commands.Commands;
import net.minecraft.network.chat.Component;
import net.minecraft.server.MinecraftServer;
import net.minecraft.server.level.ServerPlayer;
import net.minecraft.server.permissions.Permission;
import net.minecraft.server.permissions.Permissions;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;

import java.util.UUID;

/**
 * The MC# runtime, shared by the Fabric, NeoForge and Paper builds. A loader only has to call
 * {@link #init}, {@link #registerCommands} (every time a command dispatcher is built), {@link #tick} (every
 * server tick), {@link #chat} and {@link #playerLeft}. Everything else (reloads, symbols, shortcuts) is detected
 * from the tick.
 */
public final class McsRuntime {
    /** Version of the commands datapacks use (mcs probe / fast / sudo). The compiler emits the version it needs. */
    public static final int PROTOCOL = 1;
    public static final String VERSION = "1.0.0";
    public static final Logger LOG = LoggerFactory.getLogger("mcs");

    private static Platform platform;
    private static Config config = new Config();
    private static MinecraftServer server;
    private static Object lastCommands, lastLibrary;

    private McsRuntime() {}

    public static void init(Platform p) {
        platform = p;
        config = Config.load(p.configDir());
        LOG.info("MC# runtime {} ({}), protocol {}", VERSION, p.name(), PROTOCOL);
    }

    public static Config config() { return config; }
    public static MinecraftServer server() { return server; }
    public static String platformName() { return platform == null ? "?" : platform.name(); }

    /** Call whenever a command dispatcher is built (server start and every /reload). */
    public static void registerCommands(CommandDispatcher<CommandSourceStack> dispatcher) {
        McsCommands.register(dispatcher);
        Repl.register(dispatcher);
    }

    /** Call at the end of every server tick. */
    public static void tick(MinecraftServer s) {
        server = s;
        // A /reload builds a new command dispatcher and function library: refresh what depends on them
        Object commands = s.getCommands();
        Object library = Reflect.get(s.getFunctions(), "library");
        if (commands != lastCommands || library != lastLibrary) {
            lastCommands = commands;
            lastLibrary = library;
            afterReload(s);
        }
        Debugger.tick(s);
    }

    private static void afterReload(MinecraftServer s) {
        try {
            Symbols.load(s);
            Shortcuts.install(s);
            Instrument.afterReload(s);
            for (ServerPlayer p : s.getPlayerList().getPlayers()) s.getCommands().sendCommands(p);
        } catch (RuntimeException e) {
            LOG.error("MC# runtime: refreshing after a reload failed", e);
        }
    }

    /** A chat message from a player. Returns true if the runtime used it (the interpreter): cancel the message. */
    public static boolean chat(ServerPlayer player, String message) {
        return Repl.chat(player, message);
    }

    public static void playerLeft(UUID player) {
        Repl.leave(player);
        Debugger.leave(player);
    }

    // ---------------------------------------------------------------- helpers

    static Permission permission(int level) {
        return switch (level) {
            case 1 -> Permissions.COMMANDS_MODERATOR;
            case 2 -> Permissions.COMMANDS_GAMEMASTER;
            case 3 -> Permissions.COMMANDS_ADMIN;
            default -> Permissions.COMMANDS_OWNER;
        };
    }

    public static boolean hasLevel(CommandSourceStack src, int level) {
        return level <= 0 || src.permissions().hasPermission(permission(level));
    }

    /** Is this the source Minecraft parses function files with? (no server, no world) */
    public static boolean isFunctionCompilation(CommandSourceStack src) {
        return src.getServer() == null && src.getLevel() == null;
    }

    public static void info(CommandSourceStack src, String text) {
        src.sendSystemMessage(Component.literal(text));
    }

    public static void info(CommandSourceStack src, Component text) {
        src.sendSystemMessage(text);
    }

    public static void error(CommandSourceStack src, String text) {
        src.sendSystemMessage(Component.literal(text).withStyle(ChatFormatting.RED));
    }

    static Commands commands() { return server.getCommands(); }
}
