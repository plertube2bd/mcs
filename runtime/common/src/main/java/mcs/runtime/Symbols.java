package mcs.runtime;

import com.google.gson.JsonArray;
import com.google.gson.JsonElement;
import com.google.gson.JsonObject;
import com.google.gson.JsonParser;
import net.minecraft.nbt.CompoundTag;
import net.minecraft.nbt.ListTag;
import net.minecraft.nbt.NumericTag;
import net.minecraft.nbt.StringTag;
import net.minecraft.nbt.Tag;
import net.minecraft.resources.Identifier;
import net.minecraft.server.MinecraftServer;
import net.minecraft.world.scores.Objective;
import net.minecraft.world.scores.ScoreHolder;
import net.minecraft.world.scores.Scoreboard;

import java.io.Reader;
import java.util.ArrayList;
import java.util.Collections;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.TreeMap;

/** data/<ns>/mcs/symbols.json of the loaded MC# datapacks: where MC# names live at run time. */
public final class Symbols {
    private Symbols() {}

    /** A variable: a score (holder + objective) or a storage path. type: int, bool, milli, q16, string, array */
    public record Var(String name, String type, String holder, String objective, String storage, String path) {
        public boolean isScore() { return holder != null; }
    }

    public record Fn(String name, String path, boolean callable, List<Var> params, Var returns, List<Var> locals) {}

    public record Pack(String namespace, String objective, String storage, String runtime, int protocol,
                       Map<String, Var> statics, List<Fn> functions) {
        public Fn function(String name) {
            for (Fn f : functions) if (f.name.equals(name)) return f;
            return null;
        }

        public Fn functionByPath(String path) {
            for (Fn f : functions) if (f.path.equals(path)) return f;
            return null;
        }
    }

    private static volatile Map<String, Pack> packs = Map.of();

    public static Map<String, Pack> packs() { return packs; }

    public static Pack pack(String ns) { return packs.get(ns); }

    static void load(MinecraftServer s) {
        Map<String, Pack> found = new TreeMap<>();
        var rm = s.getResourceManager();
        for (String ns : rm.getNamespaces()) {
            var res = rm.getResource(Identifier.fromNamespaceAndPath(ns, "mcs/symbols.json"));
            if (res.isEmpty()) continue;
            try (Reader r = res.get().openAsReader()) {
                found.put(ns, parse(JsonParser.parseReader(r).getAsJsonObject()));
            } catch (Exception e) {
                McsRuntime.LOG.warn("MC# runtime: cannot read {}:mcs/symbols.json: {}", ns, e.toString());
            }
        }
        packs = Collections.unmodifiableMap(found);
        if (!found.isEmpty()) McsRuntime.LOG.info("MC# runtime: datapacks {}", found.keySet());
    }

    private static String str(JsonObject o, String k) {
        JsonElement e = o.get(k);
        return e == null || e.isJsonNull() ? null : e.getAsString();
    }

    private static Var var(JsonObject o) {
        return new Var(str(o, "name"), str(o, "type"), str(o, "holder"), str(o, "objective"), str(o, "storage"), str(o, "path"));
    }

    private static List<Var> vars(JsonObject o, String k) {
        List<Var> l = new ArrayList<>();
        if (o.get(k) instanceof JsonArray a) for (JsonElement e : a) l.add(var(e.getAsJsonObject()));
        return l;
    }

    private static Pack parse(JsonObject o) {
        Map<String, Var> statics = new LinkedHashMap<>();
        for (Var v : vars(o, "statics")) statics.put(v.name(), v);
        List<Fn> fns = new ArrayList<>();
        if (o.get("functions") instanceof JsonArray a)
            for (JsonElement e : a) {
                JsonObject f = e.getAsJsonObject();
                Var ret = f.get("returns") instanceof JsonObject ro ? var(ro) : null;
                fns.add(new Fn(str(f, "name"), str(f, "path"), f.has("callable") && f.get("callable").getAsBoolean(),
                    vars(f, "params"), ret, vars(f, "locals")));
            }
        return new Pack(str(o, "namespace"), str(o, "objective"), str(o, "storage"), str(o, "runtime"),
            o.has("protocol") ? o.get("protocol").getAsInt() : 0, statics, fns);
    }

    // ---------------------------------------------------------------- values

    public static int scale(String type) {
        return switch (type) { case "milli" -> 1000; case "q16" -> 65536; default -> 1; };
    }

    /** The raw score, or null if it has none. */
    public static Integer score(MinecraftServer s, String holder, String objective) {
        Scoreboard sb = s.getScoreboard();
        Objective obj = sb.getObjective(objective);
        if (obj == null) return null;
        var info = sb.getPlayerScoreInfo(ScoreHolder.forNameOnly(holder), obj);
        return info == null ? null : info.value();
    }

    public static void setScore(MinecraftServer s, String holder, String objective, int value) {
        Scoreboard sb = s.getScoreboard();
        Objective obj = sb.getObjective(objective);
        if (obj == null) throw new IllegalStateException("The objective " + objective + " does not exist (is the datapack loaded?)");
        sb.getOrCreatePlayerScore(ScoreHolder.forNameOnly(holder), obj).set(value);
    }

    /** The value of a variable as a Java object (Integer, Boolean, Double, String, List), or null if unset. */
    public static Object read(MinecraftServer s, Var v) {
        if (v.isScore()) {
            Integer raw = score(s, v.holder(), v.objective());
            if (raw == null) return null;
            return switch (v.type()) {
                case "bool" -> raw != 0;
                case "milli", "q16" -> raw / (double) scale(v.type());
                default -> raw;
            };
        }
        Tag t = storageGet(s, v.storage(), v.path());
        return t == null ? null : fromTag(t);
    }

    /** Writes a variable (numbers are converted to the variable's type). */
    public static void write(MinecraftServer s, Var v, Object value) {
        if (v.isScore()) {
            setScore(s, v.holder(), v.objective(), toScore(v.type(), value));
            return;
        }
        if (!"string".equals(v.type())) throw new IllegalArgumentException(v.name() + ": only scores and NString variables can be set");
        storagePut(s, v.storage(), v.path(), StringTag.valueOf(String.valueOf(value)));
    }

    /** A Java value → the score of an MC# variable of this type. */
    public static int toScore(String type, Object value) {
        return switch (type) {
            case "bool" -> truthy(value) ? 1 : 0;
            case "milli", "q16" -> (int) Math.round(number(value) * scale(type));
            default -> (int) number(value);
        };
    }

    static boolean truthy(Object v) {
        if (v instanceof Boolean b) return b;
        if (v instanceof Number n) return n.doubleValue() != 0;
        throw new IllegalArgumentException("Expected a bool, got " + format(v));
    }

    static double number(Object v) {
        if (v instanceof Number n) return n.doubleValue();
        if (v instanceof Boolean b) return b ? 1 : 0;
        if (v instanceof String str) {
            try { return Double.parseDouble(str.trim()); } catch (NumberFormatException ignored) {}
        }
        throw new IllegalArgumentException("Expected a number, got " + format(v));
    }

    private static Object fromTag(Tag t) {
        if (t instanceof StringTag st) return st.value();
        if (t instanceof NumericTag n) return n instanceof net.minecraft.nbt.DoubleTag || n instanceof net.minecraft.nbt.FloatTag ? (Object) n.doubleValue() : (Object) n.intValue();
        if (t instanceof ListTag l) {
            List<Object> out = new ArrayList<>();
            for (Tag e : l) out.add(fromTag(e));
            return out;
        }
        return t.toString();
    }

    private static Tag storageGet(MinecraftServer s, String storage, String path) {
        Tag cur = s.getCommandStorage().get(Identifier.parse(storage));
        for (String part : path.split("\\.")) {
            if (!(cur instanceof CompoundTag c)) return null;
            cur = c.get(part);
        }
        return cur;
    }

    private static void storagePut(MinecraftServer s, String storage, String path, Tag value) {
        Identifier id = Identifier.parse(storage);
        CompoundTag root = s.getCommandStorage().get(id);
        String[] parts = path.split("\\.");
        CompoundTag cur = root;
        for (int i = 0; i < parts.length - 1; i++) {
            CompoundTag next = cur.getCompoundOrEmpty(parts[i]);
            cur.put(parts[i], next);
            cur = next;
        }
        cur.put(parts[parts.length - 1], value);
        s.getCommandStorage().set(id, root);
    }

    /** Values as MC# prints them. */
    public static String format(Object v) {
        if (v == null) return "(unset)";
        if (v instanceof Double d) {
            if (d == Math.rint(d) && Math.abs(d) < 1e15) return String.valueOf(d.longValue());
            String t = String.format(java.util.Locale.ROOT, "%.5f", d);
            return t.replaceAll("0+$", "").replaceAll("\\.$", "");
        }
        if (v instanceof Boolean b) return b ? "True" : "False";
        if (v instanceof List<?> l) {
            StringBuilder sb = new StringBuilder("[");
            for (int i = 0; i < l.size(); i++) sb.append(i > 0 ? ", " : "").append(l.get(i) instanceof String str ? "\"" + str + "\"" : format(l.get(i)));
            return sb.append(']').toString();
        }
        return String.valueOf(v);
    }
}
