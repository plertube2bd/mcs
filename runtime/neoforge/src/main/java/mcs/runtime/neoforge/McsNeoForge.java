package mcs.runtime.neoforge;

import mcs.runtime.McsRuntime;
import mcs.runtime.Platform;
import net.neoforged.fml.common.Mod;
import net.neoforged.fml.loading.FMLPaths;
import net.neoforged.neoforge.common.NeoForge;
import net.neoforged.neoforge.event.RegisterCommandsEvent;
import net.neoforged.neoforge.event.ServerChatEvent;
import net.neoforged.neoforge.event.entity.player.PlayerEvent;
import net.neoforged.neoforge.event.tick.ServerTickEvent;

import java.nio.file.Path;

@Mod("mcs")
public final class McsNeoForge {
    public McsNeoForge() {
        McsRuntime.init(new Platform() {
            @Override public String name() { return "NeoForge"; }
            @Override public Path configDir() { return FMLPaths.CONFIGDIR.get(); }
        });
        NeoForge.EVENT_BUS.addListener((RegisterCommandsEvent e) -> McsRuntime.registerCommands(e.getDispatcher()));
        NeoForge.EVENT_BUS.addListener((ServerTickEvent.Post e) -> McsRuntime.tick(e.getServer()));
        NeoForge.EVENT_BUS.addListener((ServerChatEvent e) -> {
            if (McsRuntime.chat(e.getPlayer(), e.getRawText())) e.setCanceled(true);
        });
        NeoForge.EVENT_BUS.addListener((PlayerEvent.PlayerLoggedOutEvent e) -> McsRuntime.playerLeft(e.getEntity().getUUID()));
    }
}
