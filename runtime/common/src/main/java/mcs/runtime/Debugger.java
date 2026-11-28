package mcs.runtime;

import net.minecraft.ChatFormatting;
import net.minecraft.commands.CommandSourceStack;
import net.minecraft.network.chat.Component;
import net.minecraft.network.chat.MutableComponent;
import net.minecraft.server.MinecraftServer;
import net.minecraft.server.level.ServerPlayer;

import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.UUID;

/** /mcs packs | vars | get | set | watch | unwatch: MC# variables by name. */
public final class Debugger {
    private Debugger() {}

    private static final UUID CONSOLE = new UUID(0, 0);

    private static final class Watch {
        final String pack, name;
        String last;

        Watch(String pack, String name, String last) { this.pack = pack; this.name = name; this.last = last; }
    }

    private static final Map<UUID, List<Watch>> watches = new HashMap<>();

    static UUID who(CommandSourceStack src) {
        return src.getPlayer() != null ? src.getPlayer().getUUID() : CONSOLE;
    }

    /** A static ("count", "Cls.field") or a method's local ("Main.n"). */
    public static Symbols.Var resolve(Symbols.Pack p, String name) {
        Symbols.Var v = p.statics().get(name);
        if (v != null) return v;
        int dot = name.lastIndexOf('.');
        if (dot > 0) {
            String fn = name.substring(0, dot), local = name.substring(dot + 1);
            for (Symbols.Fn f : p.functions())
                if (f.name().equals(fn)) {
                    for (Symbols.Var l : f.locals()) if (l.name().equals(local)) return l;
                    for (Symbols.Var l : f.params()) if (l.name().equals(local) && l.isScore()) return l;
                }
        }
        return null;
    }

    private static Symbols.Pack pack(CommandSourceStack src, String ns) {
        Symbols.Pack p = Symbols.pack(ns);
        if (p == null) McsRuntime.error(src, "No MC# datapack '" + ns + "' is loaded (built with the runtime on, Minecraft 26.1+). Loaded: " + Symbols.packs().keySet());
        return p;
    }

    private static MutableComponent line(String name, String type, Object value) {
        return Component.literal("  " + name).withStyle(ChatFormatting.AQUA)
            .append(Component.literal(" : " + type).withStyle(ChatFormatting.GRAY))
            .append(Component.literal(" = " + Symbols.format(value)).withStyle(ChatFormatting.WHITE));
    }

    static int packs(CommandSourceStack src) {
        if (Symbols.packs().isEmpty()) {
            McsRuntime.info(src, "No MC# datapacks with runtime symbols are loaded.");
            return 0;
        }
        for (Symbols.Pack p : Symbols.packs().values())
            McsRuntime.info(src, Component.literal(p.namespace()).withStyle(ChatFormatting.GOLD)
                .append(Component.literal("  runtime=" + p.runtime() + ", " + p.statics().size() + " statics, " + p.functions().size() + " methods").withStyle(ChatFormatting.GRAY)));
        return Symbols.packs().size();
    }

    static int vars(CommandSourceStack src, String ns, String method) {
        Symbols.Pack p = pack(src, ns);
        if (p == null) return 0;
        MinecraftServer s = src.getServer();
        int n = 0;
        if (method.isEmpty()) {
            McsRuntime.info(src, Component.literal(ns + " statics").withStyle(ChatFormatting.GOLD));
            for (Symbols.Var v : p.statics().values()) { McsRuntime.info(src, line(v.name(), v.type(), Symbols.read(s, v))); n++; }
            if (n == 0) McsRuntime.info(src, "  (none)");
            return n;
        }
        for (Symbols.Fn f : p.functions()) {
            if (!f.name().equals(method)) continue;
            McsRuntime.info(src, Component.literal(ns + " " + f.name() + " (" + f.path() + ")").withStyle(ChatFormatting.GOLD));
            for (Symbols.Var v : f.params()) if (v.isScore()) { McsRuntime.info(src, line(v.name(), v.type() + " param", Symbols.read(s, v))); n++; }
            for (Symbols.Var v : f.locals()) { McsRuntime.info(src, line(v.name(), v.type(), Symbols.read(s, v))); n++; }
            if (f.returns() != null) McsRuntime.info(src, line("return", f.returns().type(), Symbols.read(s, f.returns())));
        }
        if (n == 0) McsRuntime.info(src, "  (no variables)");
        return n;
    }

    static int get(CommandSourceStack src, String ns, String name) {
        Symbols.Pack p = pack(src, ns);
        if (p == null) return 0;
        Symbols.Var v = resolve(p, name);
        if (v == null) { McsRuntime.error(src, "No variable '" + name + "' in " + ns); return 0; }
        Object val = Symbols.read(src.getServer(), v);
        McsRuntime.info(src, line(ns + ":" + name, v.type(), val));
        return val instanceof Integer i ? i : val instanceof Boolean b ? (b ? 1 : 0) : 1;
    }

    static int set(CommandSourceStack src, String ns, String name, String value) {
        Symbols.Pack p = pack(src, ns);
        if (p == null) return 0;
        Symbols.Var v = resolve(p, name);
        if (v == null) { McsRuntime.error(src, "No variable '" + name + "' in " + ns); return 0; }
        try {
            Object val = switch (v.type()) {
                case "bool" -> Boolean.parseBoolean(value.trim());
                case "string" -> value.startsWith("\"") && value.endsWith("\"") && value.length() >= 2 ? value.substring(1, value.length() - 1) : value;
                default -> Double.parseDouble(value.trim());
            };
            Symbols.write(src.getServer(), v, val);
        } catch (RuntimeException e) {
            McsRuntime.error(src, "Cannot set " + name + ": " + e.getMessage());
            return 0;
        }
        McsRuntime.info(src, line(ns + ":" + name, v.type(), Symbols.read(src.getServer(), v)));
        return 1;
    }

    static int watch(CommandSourceStack src, String ns, String name) {
        Symbols.Pack p = pack(src, ns);
        if (p == null) return 0;
        Symbols.Var v = resolve(p, name);
        if (v == null) { McsRuntime.error(src, "No variable '" + name + "' in " + ns); return 0; }
        List<Watch> l = watches.computeIfAbsent(who(src), k -> new ArrayList<>());
        l.removeIf(w -> w.pack.equals(ns) && w.name.equals(name));
        String now = Symbols.format(Symbols.read(src.getServer(), v));
        l.add(new Watch(ns, name, now));
        McsRuntime.info(src, Component.literal("[watch] " + ns + ":" + name + " = " + now + "  (changes are shown here; /mcs unwatch to stop)").withStyle(ChatFormatting.YELLOW));
        return 1;
    }

    static int unwatch(CommandSourceStack src, String ns, String name) {
        List<Watch> l = watches.get(who(src));
        if (l == null) return 0;
        int before = l.size();
        if (ns == null) l.clear(); else l.removeIf(w -> w.pack.equals(ns) && w.name.equals(name));
        McsRuntime.info(src, "[watch] stopped " + (before - l.size()) + " watch(es)");
        return before - l.size();
    }

    static int listWatches(CommandSourceStack src) {
        List<Watch> l = watches.getOrDefault(who(src), List.of());
        if (l.isEmpty()) McsRuntime.info(src, "[watch] nothing is watched. /mcs watch <pack> <variable>");
        for (Watch w : l) McsRuntime.info(src, "[watch] " + w.pack + ":" + w.name + " = " + w.last);
        return l.size();
    }

    static void tick(MinecraftServer s) {
        if (watches.isEmpty()) return;
        for (var e : watches.entrySet()) {
            for (Watch w : e.getValue()) {
                Symbols.Pack p = Symbols.pack(w.pack);
                Symbols.Var v = p == null ? null : resolve(p, w.name);
                String now = v == null ? "(gone)" : Symbols.format(Symbols.read(s, v));
                if (now.equals(w.last)) continue;
                String msg = "[watch] " + w.pack + ":" + w.name + " " + w.last + " → " + now + "  (tick " + s.getTickCount() + ")";
                w.last = now;
                if (e.getKey().equals(CONSOLE)) McsRuntime.LOG.info(msg);
                else {
                    ServerPlayer pl = s.getPlayerList().getPlayer(e.getKey());
                    if (pl != null) pl.sendSystemMessage(Component.literal(msg).withStyle(ChatFormatting.YELLOW));
                }
            }
        }
    }

    static void leave(UUID player) { watches.remove(player); }
}
