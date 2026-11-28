package mcs.runtime;

import com.mojang.brigadier.CommandDispatcher;
import com.mojang.brigadier.context.CommandContext;
import com.mojang.brigadier.context.ContextChain;
import com.mojang.brigadier.exceptions.CommandSyntaxException;
import com.mojang.brigadier.tree.CommandNode;
import com.mojang.brigadier.tree.LiteralCommandNode;
import com.mojang.brigadier.tree.RootCommandNode;
import net.minecraft.commands.CommandSourceStack;
import net.minecraft.commands.execution.ChainModifiers;
import net.minecraft.commands.execution.CustomCommandExecutor;
import net.minecraft.commands.execution.ExecutionControl;
import net.minecraft.network.chat.Component;
import net.minecraft.resources.Identifier;
import net.minecraft.server.MinecraftServer;

import java.util.ArrayList;
import java.util.List;
import java.util.Map;

import static com.mojang.brigadier.arguments.StringArgumentType.getString;
import static com.mojang.brigadier.arguments.StringArgumentType.greedyString;
import static net.minecraft.commands.Commands.argument;
import static net.minecraft.commands.Commands.literal;

/**
 * /<namespace>:<function> [macro arguments] runs a datapack function (like /function, same permission).
 * Hidden: paths with a segment starting with "__", minecraft:*, and in MC# packs the compiler's own
 * sub-functions (main/_fn1, _wait0 …: "_" segments that are not MC# methods).
 */
final class Shortcuts {
    private Shortcuts() {}

    private static final List<LiteralCommandNode<CommandSourceStack>> installed = new ArrayList<>();
    private static CommandDispatcher<CommandSourceStack> installedIn;

    static boolean hidden(Identifier id) {
        if (id.getNamespace().equals("minecraft")) return true;
        String[] segs = id.getPath().split("/");
        for (String s : segs) if (s.startsWith("__")) return true;
        Symbols.Pack p = Symbols.pack(id.getNamespace());
        if (p != null && p.functionByPath(id.toString()) == null)
            for (String s : segs) if (s.startsWith("_")) return true;
        return false;
    }

    static void install(MinecraftServer s) {
        CommandDispatcher<CommandSourceStack> d = s.getCommands().getDispatcher();
        if (installedIn == d) removeInstalled(d);   // same dispatcher (e.g. only the functions were reloaded)
        installed.clear();
        installedIn = d;
        if (!McsRuntime.config().shortcuts) return;
        RootCommandNode<CommandSourceStack> root = d.getRoot();
        int n = 0;
        for (Identifier id : s.getFunctions().getFunctionNames()) {
            if (hidden(id)) continue;
            String name = id.toString();
            if (root.getChild(name) != null) continue;   // never shadow a real command
            String fn = name;
            LiteralCommandNode<CommandSourceStack> node = literal(name)
                .requires(src -> McsRuntime.hasLevel(src, 2))
                .executes(new Run(fn, false))
                .then(argument("arguments", greedyString()).executes(new Run(fn, true)))
                .build();
            root.addChild(node);
            installed.add(node);
            n++;
        }
        if (n > 0) McsRuntime.LOG.info("MC# runtime: {} function shortcuts (/<namespace>:<function>)", n);
    }

    @SuppressWarnings("unchecked")
    private static void removeInstalled(CommandDispatcher<CommandSourceStack> d) {
        RootCommandNode<CommandSourceStack> root = d.getRoot();
        Map<String, CommandNode<CommandSourceStack>> children = (Map<String, CommandNode<CommandSourceStack>>) Reflect.get(root, "children");
        Map<String, ?> literals = (Map<String, ?>) Reflect.get(root, "literals");
        for (var node : installed) {
            if (children.get(node.getName()) == node) {
                children.remove(node.getName());
                literals.remove(node.getName());
            }
        }
    }

    /** Runs "function <id> [arguments]" in order, like the /function command. */
    private record Run(String function, boolean withArgs) implements CustomCommandExecutor.CommandAdapter<CommandSourceStack> {
        private String command(CommandContext<CommandSourceStack> c) {
            return "function " + function + (withArgs ? " " + getString(c, "arguments") : "");
        }

        @Override
        public void run(CommandSourceStack src, ContextChain<CommandSourceStack> chain, ChainModifiers mods, ExecutionControl<CommandSourceStack> control) {
            try {
                McsCommands.queue(src, command(chain.getTopContext()), control);
            } catch (CommandSyntaxException e) {
                src.sendFailure(Component.literal(e.getMessage()));
            }
        }

        @Override
        public int run(CommandContext<CommandSourceStack> c) {
            c.getSource().getServer().getCommands().performPrefixedCommand(c.getSource(), command(c));
            return 1;
        }
    }
}
