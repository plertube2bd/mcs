package mcs.runtime;

import com.mojang.brigadier.CommandDispatcher;
import net.minecraft.ChatFormatting;
import net.minecraft.commands.CommandSourceStack;
import net.minecraft.commands.FunctionInstantiationException;
import net.minecraft.commands.execution.UnboundEntryAction;
import net.minecraft.commands.functions.CommandFunction;
import net.minecraft.commands.functions.InstantiatedFunction;
import net.minecraft.nbt.CompoundTag;
import net.minecraft.network.chat.Component;
import net.minecraft.resources.Identifier;
import net.minecraft.server.MinecraftServer;
import net.minecraft.server.level.ServerPlayer;

import java.util.ArrayList;
import java.util.Collections;
import java.util.HashMap;
import java.util.IdentityHashMap;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.UUID;
import java.util.WeakHashMap;

/**
 * /mcs trace | profile: while active, the function library's functions (and the #tick list) are replaced by
 * wrappers that report each call and time each command. Stopping puts the originals back, so nothing is
 * slowed down otherwise. Function references are resolved when they run, so the swap takes effect at once.
 */
final class Instrument {
    private Instrument() {}

    private enum Mode { NONE, TRACE, PROFILE }

    private static final class Stat { long calls, commands, nanos; }

    private static final int MAX_TRACE = 300;
    private static Mode mode = Mode.NONE;
    private static String filter = "";
    private static UUID owner;
    private static int traceLines;
    private static final Map<Identifier, Stat> stats = new HashMap<>();
    private static final Map<Identifier, Stat> lastStats = new LinkedHashMap<>();

    // what was replaced (to restore)
    private static Object library;
    private static Map<Identifier, CommandFunction<CommandSourceStack>> savedFunctions;
    private static Map<Identifier, List<CommandFunction<CommandSourceStack>>> savedTags;
    private static List<CommandFunction<CommandSourceStack>> savedTicking;

    private static boolean matches(Identifier id) {
        return filter.isEmpty() || id.getNamespace().equals(filter) || id.toString().startsWith(filter);
    }

    static String nameOf(Identifier id) {
        Symbols.Pack p = Symbols.pack(id.getNamespace());
        Symbols.Fn f = p == null ? null : p.functionByPath(id.toString());
        return f != null ? f.name() + "  (" + id + ")" : id.toString();
    }

    // ---------------------------------------------------------------- commands

    static int startTrace(CommandSourceStack src, String f) { return start(src, Mode.TRACE, f); }

    static int startProfile(CommandSourceStack src, String f) { return start(src, Mode.PROFILE, f); }

    private static int start(CommandSourceStack src, Mode m, String f) {
        MinecraftServer s = src.getServer();
        if (mode != Mode.NONE) restore(s);
        mode = m;
        filter = f;
        owner = Debugger.who(src);
        traceLines = 0;
        stats.clear();
        apply(s);
        McsRuntime.info(src, Component.literal(m == Mode.TRACE
            ? "[trace] function calls" + (f.isEmpty() ? "" : " in " + f) + " are shown here (up to " + MAX_TRACE + " lines). /mcs trace stop"
            : "[profile] measuring" + (f.isEmpty() ? "" : " " + f) + " … /mcs profile stop to see the results").withStyle(ChatFormatting.YELLOW));
        return 1;
    }

    static int stop(CommandSourceStack src) {
        if (mode == Mode.NONE) { McsRuntime.info(src, "Nothing is being traced or profiled."); return 0; }
        Mode was = mode;
        restore(src.getServer());
        mode = Mode.NONE;
        lastStats.clear();
        lastStats.putAll(stats);
        if (was == Mode.PROFILE) return show(src);
        McsRuntime.info(src, "[trace] stopped");
        return 1;
    }

    static int show(CommandSourceStack src) {
        Map<Identifier, Stat> data = mode == Mode.PROFILE ? stats : lastStats;
        if (data.isEmpty()) { McsRuntime.info(src, "[profile] no data (/mcs profile start)"); return 0; }
        List<Map.Entry<Identifier, Stat>> rows = new ArrayList<>(data.entrySet());
        rows.sort((a, b) -> Long.compare(b.getValue().nanos, a.getValue().nanos));
        long total = 0;
        for (var r : rows) total += r.getValue().nanos;
        McsRuntime.info(src, Component.literal(String.format(java.util.Locale.ROOT, "[profile] %d functions, %.2f ms in their commands (self time)", rows.size(), total / 1e6)).withStyle(ChatFormatting.GOLD));
        McsRuntime.info(src, Component.literal("      ms   calls   cmds  function").withStyle(ChatFormatting.GRAY));
        for (int i = 0; i < Math.min(20, rows.size()); i++) {
            var r = rows.get(i);
            Stat st = r.getValue();
            McsRuntime.info(src, String.format(java.util.Locale.ROOT, "%8.2f %7d %6d  %s", st.nanos / 1e6, st.calls, st.commands, nameOf(r.getKey())));
        }
        return rows.size();
    }

    // ---------------------------------------------------------------- swapping the library

    @SuppressWarnings("unchecked")
    private static void apply(MinecraftServer s) {
        var manager = s.getFunctions();
        library = Reflect.get(manager, "library");
        savedFunctions = (Map<Identifier, CommandFunction<CommandSourceStack>>) Reflect.get(library, "functions");
        savedTags = (Map<Identifier, List<CommandFunction<CommandSourceStack>>>) Reflect.get(library, "tags");
        savedTicking = (List<CommandFunction<CommandSourceStack>>) Reflect.get(manager, "ticking");

        Map<CommandFunction<CommandSourceStack>, CommandFunction<CommandSourceStack>> wrapped = new IdentityHashMap<>();
        Map<Identifier, CommandFunction<CommandSourceStack>> fns = new HashMap<>();
        for (var e : savedFunctions.entrySet()) {
            CommandFunction<CommandSourceStack> f = e.getValue();
            CommandFunction<CommandSourceStack> w = matches(e.getKey()) ? new Wrapped(f) : f;
            wrapped.put(f, w);
            fns.put(e.getKey(), w);
        }
        Map<Identifier, List<CommandFunction<CommandSourceStack>>> tags = new HashMap<>();
        for (var e : savedTags.entrySet()) tags.put(e.getKey(), e.getValue().stream().map(f -> wrapped.getOrDefault(f, f)).toList());
        Reflect.set(library, "functions", Collections.unmodifiableMap(fns));
        Reflect.set(library, "tags", Collections.unmodifiableMap(tags));
        Reflect.set(manager, "ticking", savedTicking.stream().map(f -> wrapped.getOrDefault(f, f)).toList());
    }

    private static void restore(MinecraftServer s) {
        var manager = s.getFunctions();
        if (library != null && Reflect.get(manager, "library") == library) {
            Reflect.set(library, "functions", savedFunctions);
            Reflect.set(library, "tags", savedTags);
            Reflect.set(manager, "ticking", savedTicking);
        }
        library = null;
        savedFunctions = null;
        savedTags = null;
        savedTicking = null;
    }

    /** A /reload replaced the library: instrument the new one. */
    static void afterReload(MinecraftServer s) {
        if (mode == Mode.NONE) return;
        library = null;
        apply(s);
    }

    // ---------------------------------------------------------------- the wrappers

    private static void report(String text) {
        MinecraftServer s = McsRuntime.server();
        if (owner == null || s == null) return;
        ServerPlayer p = s.getPlayerList().getPlayer(owner);
        if (p != null) p.sendSystemMessage(Component.literal(text).withStyle(ChatFormatting.GRAY));
        else McsRuntime.LOG.info(text);
    }

    private static void enter(Identifier id, int depth) {
        stats.computeIfAbsent(id, k -> new Stat()).calls++;
        if (mode != Mode.TRACE) return;
        if (traceLines++ < MAX_TRACE) {
            MinecraftServer s = McsRuntime.server();
            report("[t" + (s == null ? "?" : s.getTickCount()) + "] " + "  ".repeat(Math.min(depth, 20)) + nameOf(id));
        } else if (traceLines == MAX_TRACE + 1) {
            report("[trace] " + MAX_TRACE + " lines shown; stopping (/mcs trace start to trace again)");
            MinecraftServer s = McsRuntime.server();
            if (s != null) s.execute(() -> { if (mode == Mode.TRACE) { restore(s); mode = Mode.NONE; } });
        }
    }

    private static final class Wrapped implements CommandFunction<CommandSourceStack> {
        private final CommandFunction<CommandSourceStack> delegate;
        private final Map<InstantiatedFunction<CommandSourceStack>, InstantiatedFunction<CommandSourceStack>> cache = Collections.synchronizedMap(new WeakHashMap<>());

        Wrapped(CommandFunction<CommandSourceStack> delegate) { this.delegate = delegate; }

        @Override
        public Identifier id() { return delegate.id(); }

        @Override
        public InstantiatedFunction<CommandSourceStack> instantiate(CompoundTag args, CommandDispatcher<CommandSourceStack> dispatcher) throws FunctionInstantiationException {
            InstantiatedFunction<CommandSourceStack> inst = delegate.instantiate(args, dispatcher);
            return cache.computeIfAbsent(inst, Instrument::wrap);
        }
    }

    private static InstantiatedFunction<CommandSourceStack> wrap(InstantiatedFunction<CommandSourceStack> inst) {
        Identifier id = inst.id();
        List<UnboundEntryAction<CommandSourceStack>> entries = new ArrayList<>();
        entries.add((src, ctx, frame) -> enter(id, frame.depth()));
        for (UnboundEntryAction<CommandSourceStack> e : inst.entries()) {
            entries.add((src, ctx, frame) -> {
                long t0 = System.nanoTime();
                try {
                    e.execute(src, ctx, frame);
                } finally {
                    Stat st = stats.computeIfAbsent(id, k -> new Stat());
                    st.commands++;
                    st.nanos += System.nanoTime() - t0;
                }
            });
        }
        List<UnboundEntryAction<CommandSourceStack>> fixed = List.copyOf(entries);
        return new InstantiatedFunction<>() {
            @Override public Identifier id() { return id; }
            @Override public List<UnboundEntryAction<CommandSourceStack>> entries() { return fixed; }
        };
    }
}
