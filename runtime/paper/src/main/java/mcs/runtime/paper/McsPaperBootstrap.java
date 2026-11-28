package mcs.runtime.paper;

import com.mojang.brigadier.CommandDispatcher;
import io.papermc.paper.plugin.bootstrap.BootstrapContext;
import io.papermc.paper.plugin.bootstrap.PluginBootstrap;
import io.papermc.paper.plugin.lifecycle.event.types.LifecycleEvents;
import mcs.runtime.McsRuntime;
import mcs.runtime.Platform;
import net.minecraft.commands.CommandSourceStack;

import java.nio.file.Path;

/**
 * Commands are registered during bootstrap so datapack functions can use them (Paper builds the dispatcher
 * that parses functions from the COMMANDS lifecycle event, on start and on every /reload).
 */
@SuppressWarnings("UnstableApiUsage")
public final class McsPaperBootstrap implements PluginBootstrap {
    @Override
    public void bootstrap(BootstrapContext context) {
        Path dir = context.getDataDirectory();
        McsRuntime.init(new Platform() {
            @Override public String name() { return "Paper"; }
            @Override public Path configDir() { return dir; }
        });
        context.getLifecycleManager().registerEventHandler(LifecycleEvents.COMMANDS, event -> {
            // Paper's command source is the vanilla CommandSourceStack at run time
            @SuppressWarnings("unchecked")
            CommandDispatcher<CommandSourceStack> d = (CommandDispatcher<CommandSourceStack>) (CommandDispatcher<?>) event.registrar().getDispatcher();
            McsRuntime.registerCommands(d);
        });
    }
}
