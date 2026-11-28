package mcs.runtime;

import java.lang.reflect.Field;

/** Field access for the few internals the runtime touches (Minecraft 26.x is not obfuscated). */
public final class Reflect {
    private Reflect() {}

    private static ThreadLocal<Object> currentContext;

    /**
     * Runs code as if no command were executing: functions and commands started from it run to completion
     * right away (inside a command they would only be queued after the current one).
     */
    @SuppressWarnings("unchecked")
    public static void outsideCommands(Runnable r) {
        if (currentContext == null) {
            try {
                currentContext = (ThreadLocal<Object>) field(net.minecraft.commands.Commands.class, "CURRENT_EXECUTION_CONTEXT").get(null);
            } catch (ReflectiveOperationException e) {
                throw new IllegalStateException(e);
            }
        }
        Object saved = currentContext.get();
        currentContext.set(null);
        try { r.run(); } finally { currentContext.set(saved); }
    }

    private static final java.util.Map<String, Field> cache = new java.util.concurrent.ConcurrentHashMap<>();

    static Field field(Class<?> c, String name) {
        return cache.computeIfAbsent(c.getName() + "#" + name, k -> find(c, name));
    }

    private static Field find(Class<?> c, String name) {
        for (Class<?> k = c; k != null; k = k.getSuperclass()) {
            try {
                Field f = k.getDeclaredField(name);
                f.setAccessible(true);
                return f;
            } catch (NoSuchFieldException ignored) {
            }
        }
        throw new IllegalStateException("No field " + name + " in " + c.getName());
    }

    static Object get(Object o, String name) {
        try { return field(o.getClass(), name).get(o); } catch (ReflectiveOperationException e) { throw new IllegalStateException(e); }
    }

    static void set(Object o, String name, Object value) {
        try { field(o.getClass(), name).set(o, value); } catch (ReflectiveOperationException e) { throw new IllegalStateException(e); }
    }
}
