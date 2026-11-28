package mcs.runtime.paper;

import com.destroystokyo.paper.event.server.ServerTickEndEvent;
import io.papermc.paper.event.player.AsyncChatEvent;
import mcs.runtime.McsRuntime;
import net.kyori.adventure.text.serializer.plain.PlainTextComponentSerializer;
import org.bukkit.craftbukkit.CraftServer;
import org.bukkit.craftbukkit.entity.CraftPlayer;
import org.bukkit.event.EventHandler;
import org.bukkit.event.EventPriority;
import org.bukkit.event.Listener;
import org.bukkit.event.player.PlayerQuitEvent;
import org.bukkit.plugin.java.JavaPlugin;

public final class McsPaperPlugin extends JavaPlugin implements Listener {
    @Override
    public void onEnable() {
        getServer().getPluginManager().registerEvents(this, this);
    }

    @EventHandler
    public void onTickEnd(ServerTickEndEvent e) {
        McsRuntime.tick(((CraftServer) getServer()).getServer());
    }

    @EventHandler(priority = EventPriority.LOWEST, ignoreCancelled = true)
    public void onChat(AsyncChatEvent e) {
        String text = PlainTextComponentSerializer.plainText().serialize(e.originalMessage());
        if (McsRuntime.chat(((CraftPlayer) e.getPlayer()).getHandle(), text)) e.setCancelled(true);
    }

    @EventHandler
    public void onQuit(PlayerQuitEvent e) {
        McsRuntime.playerLeft(e.getPlayer().getUniqueId());
    }
}
