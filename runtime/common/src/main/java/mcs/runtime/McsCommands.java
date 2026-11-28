package mcs.runtime;

import com.mojang.brigadier.CommandDispatcher;
import com.mojang.brigadier.StringReader;
import com.mojang.brigadier.arguments.IntegerArgumentType;
import com.mojang.brigadier.arguments.StringArgumentType;
import com.mojang.brigadier.builder.LiteralArgumentBuilder;
import com.mojang.brigadier.context.CommandContext;
import com.mojang.brigadier.context.ContextChain;
import com.mojang.brigadier.exceptions.CommandSyntaxException;
import com.mojang.brigadier.suggestion.SuggestionProvider;
import net.minecraft.commands.CommandSourceStack;
import net.minecraft.commands.Commands;
import net.minecraft.commands.SharedSuggestionProvider;
import net.minecraft.commands.execution.ChainModifiers;
import net.minecraft.commands.execution.CustomCommandExecutor;
import net.minecraft.commands.execution.ExecutionControl;
import net.minecraft.commands.execution.UnboundEntryAction;
import net.minecraft.commands.functions.CommandFunction;
import net.minecraft.network.chat.Component;
import net.minecraft.server.permissions.LevelBasedPermissionSet;

import java.util.ArrayList;
import java.util.List;

import static com.mojang.brigadier.arguments.StringArgumentType.getString;
import static com.mojang.brigadier.arguments.StringArgumentType.greedyString;
import static com.mojang.brigadier.arguments.StringArgumentType.word;
import static net.minecraft.commands.Commands.argument;
import static net.minecraft.commands.Commands.literal;

/** /mcs: the commands datapacks use (probe, fast, sudo) and the debugger. */
final class McsCommands {
    private McsCommands() {}

    static final SuggestionProvider<CommandSourceStack> PACKS = (c, b) -> SharedSuggestionProvider.suggest(Symbols.packs().keySet(), b);
    static final SuggestionProvider<CommandSourceStack> VARS = (c, b) -> {
        var p = Symbols.pack(getString(c, "pack"));
        List<String> names = new ArrayList<>();
        if (p != null) {
            names.addAll(p.statics().keySet());
            for (var f : p.functions()) for (var l : f.locals()) names.add(f.name() + "." + l.name());
        }
        return SharedSuggestionProvider.suggest(names, b);
    };
    static final SuggestionProvider<CommandSourceStack> FUNCTIONS = (c, b) -> {
        var p = Symbols.pack(getString(c, "pack"));
        return SharedSuggestionProvider.suggest(p == null ? List.of() : p.functions().stream().map(Symbols.Fn::name).distinct().toList(), b);
    };

    static void register(CommandDispatcher<CommandSourceStack> d) {
        int dbg = McsRuntime.config().debugLevel;
        LiteralArgumentBuilder<CommandSourceStack> mcs = literal("mcs")
            .requires(s -> McsRuntime.isFunctionCompilation(s) || McsRuntime.hasLevel(s, 2));

        // ---- used by datapacks (on macro lines, so packs load without the mod)
        mcs.then(literal("probe").then(argument("protocol", IntegerArgumentType.integer()).executes(c -> {
            int want = IntegerArgumentType.getInteger(c, "protocol");
            if (want > McsRuntime.PROTOCOL) {
                McsRuntime.LOG.warn("MC# runtime: a datapack needs runtime protocol {}, this mcs.jar has {}. Update the mod.", want, McsRuntime.PROTOCOL);
                return 0;
            }
            return McsRuntime.PROTOCOL;
        })));
        mcs.then(literal("fast").then(argument("args", greedyString()).executes(c -> {
            String[] a = getString(c, "args").trim().split(" +");
            try {
                String[] rest = new String[a.length - 1];
                System.arraycopy(a, 1, rest, 0, rest.length);
                return Fast.run(c.getSource().getServer(), a[0], rest);
            } catch (RuntimeException e) {
                c.getSource().sendFailure(Component.literal("mcs fast: " + e.getMessage()));
                return 0;
            }
        })));
        // Only function files can contain it: they are parsed with a source that has no server
        mcs.then(literal("sudo").requires(s -> McsRuntime.isFunctionCompilation(s) && McsRuntime.config().sudoEnabled)
            .then(argument("command", greedyString()).executes(new Sudo())));

        // ---- information and the debugger
        mcs.then(literal("version").executes(c -> {
            McsRuntime.info(c.getSource(), "MC# runtime " + McsRuntime.VERSION + " (" + McsRuntime.platformName() + "), protocol " + McsRuntime.PROTOCOL);
            return McsRuntime.PROTOCOL;
        }));
        mcs.then(literal("packs").requires(s -> McsRuntime.hasLevel(s, dbg)).executes(c -> Debugger.packs(c.getSource())));
        mcs.then(literal("vars").requires(s -> McsRuntime.hasLevel(s, dbg))
            .then(argument("pack", word()).suggests(PACKS).executes(c -> Debugger.vars(c.getSource(), getString(c, "pack"), ""))
                .then(argument("filter", word()).suggests(FUNCTIONS).executes(c -> Debugger.vars(c.getSource(), getString(c, "pack"), getString(c, "filter"))))));
        mcs.then(literal("get").requires(s -> McsRuntime.hasLevel(s, dbg))
            .then(argument("pack", word()).suggests(PACKS).then(argument("name", word()).suggests(VARS)
                .executes(c -> Debugger.get(c.getSource(), getString(c, "pack"), getString(c, "name"))))));
        mcs.then(literal("set").requires(s -> McsRuntime.hasLevel(s, dbg))
            .then(argument("pack", word()).suggests(PACKS).then(argument("name", word()).suggests(VARS)
                .then(argument("value", greedyString()).executes(c -> Debugger.set(c.getSource(), getString(c, "pack"), getString(c, "name"), getString(c, "value")))))));
        mcs.then(literal("watch").requires(s -> McsRuntime.hasLevel(s, dbg))
            .executes(c -> Debugger.listWatches(c.getSource()))
            .then(argument("pack", word()).suggests(PACKS).then(argument("name", word()).suggests(VARS)
                .executes(c -> Debugger.watch(c.getSource(), getString(c, "pack"), getString(c, "name"))))));
        mcs.then(literal("unwatch").requires(s -> McsRuntime.hasLevel(s, dbg))
            .executes(c -> Debugger.unwatch(c.getSource(), null, null))
            .then(argument("pack", word()).suggests(PACKS).then(argument("name", word()).suggests(VARS)
                .executes(c -> Debugger.unwatch(c.getSource(), getString(c, "pack"), getString(c, "name"))))));
        mcs.then(literal("trace").requires(s -> McsRuntime.hasLevel(s, dbg))
            .then(literal("start").executes(c -> Instrument.startTrace(c.getSource(), ""))
                .then(argument("filter", word()).suggests(PACKS).executes(c -> Instrument.startTrace(c.getSource(), getString(c, "filter")))))
            .then(literal("stop").executes(c -> Instrument.stop(c.getSource()))));
        mcs.then(literal("profile").requires(s -> McsRuntime.hasLevel(s, dbg))
            .then(literal("start").executes(c -> Instrument.startProfile(c.getSource(), ""))
                .then(argument("filter", word()).suggests(PACKS).executes(c -> Instrument.startProfile(c.getSource(), getString(c, "filter")))))
            .then(literal("stop").executes(c -> Instrument.stop(c.getSource())))
            .then(literal("show").executes(c -> Instrument.show(c.getSource()))));
        d.register(mcs);
    }

    /** Queues a command into the running function, right after the current one (like /function does). */
    static void queue(CommandSourceStack src, String command, ExecutionControl<CommandSourceStack> control) throws CommandSyntaxException {
        var dispatcher = src.getServer().getCommands().getDispatcher();
        UnboundEntryAction<CommandSourceStack> action = CommandFunction.parseCommand(dispatcher, src, new StringReader(command));
        control.queueNext(action.bind(src));
    }

    /** mcs sudo <command>: the command runs with permission level 4, in order with the rest of the function. */
    static final class Sudo implements CustomCommandExecutor.CommandAdapter<CommandSourceStack> {
        @Override
        public void run(CommandSourceStack src, ContextChain<CommandSourceStack> chain, ChainModifiers mods, ExecutionControl<CommandSourceStack> control) {
            String cmd = Commands.trimOptionalPrefix(getString(chain.getTopContext(), "command"));
            if (!McsRuntime.config().sudoEnabled) {
                src.sendFailure(Component.literal("mcs sudo is disabled (sudo.enabled in mcs-runtime.properties)"));
                return;
            }
            if (McsRuntime.config().sudoLog) McsRuntime.LOG.info("[mcs sudo] {}", cmd);
            try {
                queue(src.withPermission(LevelBasedPermissionSet.OWNER), cmd, control);
            } catch (CommandSyntaxException e) {
                src.sendFailure(Component.literal("mcs sudo: " + e.getMessage()));
            }
        }

        /** Fallback when a loader calls the command directly: run it right away. */
        @Override
        public int run(CommandContext<CommandSourceStack> c) {
            CommandSourceStack src = c.getSource().withPermission(LevelBasedPermissionSet.OWNER);
            String cmd = Commands.trimOptionalPrefix(getString(c, "command"));
            if (!McsRuntime.config().sudoEnabled) return 0;
            if (McsRuntime.config().sudoLog) McsRuntime.LOG.info("[mcs sudo] {}", cmd);
            src.getServer().getCommands().performPrefixedCommand(src, cmd);
            return 1;
        }
    }
}
