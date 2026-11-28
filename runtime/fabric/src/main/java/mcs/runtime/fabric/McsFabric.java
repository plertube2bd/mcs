package mcs.runtime.fabric;

import mcs.runtime.McsRuntime;
import mcs.runtime.Platform;
import net.fabricmc.api.ModInitializer;
import net.fabricmc.fabric.api.command.v2.CommandRegistrationCallback;
import net.fabricmc.fabric.api.event.lifecycle.v1.ServerTickEvents;
import net.fabricmc.fabric.api.message.v1.ServerMessageEvents;
import net.fabricmc.fabric.api.networking.v1.ServerPlayConnectionEvents;
import net.fabricmc.loader.api.FabricLoader;

import java.nio.file.Path;

public final class McsFabric implements ModInitializer {
    @Override
    public void onInitialize() {
        McsRuntime.init(new Platform() {
            @Override public String name() { return "Fabric"; }
            @Override public Path configDir() { return FabricLoader.getInstance().getConfigDir(); }
        });
        CommandRegistrationCallback.EVENT.register((dispatcher, registries, environment) -> McsRuntime.registerCommands(dispatcher));
        ServerTickEvents.END_SERVER_TICK.register(McsRuntime::tick);
        ServerMessageEvents.ALLOW_CHAT_MESSAGE.register((message, sender, params) -> !McsRuntime.chat(sender, message.signedContent()));
        ServerPlayConnectionEvents.DISCONNECT.register((handler, server) -> McsRuntime.playerLeft(handler.getPlayer().getUUID()));
    }
}
