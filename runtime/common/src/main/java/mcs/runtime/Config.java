package mcs.runtime;

import java.io.IOException;
import java.io.Reader;
import java.io.Writer;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.Properties;

/** config/mcs-runtime.properties (created with the defaults on first start). */
public final class Config {
    /** Datapack functions may run permission level 3–4 commands with `mcs sudo` (Runtime.Sudo in MC#). */
    public boolean sudoEnabled = true;
    /** Write every sudo command to the server log. */
    public boolean sudoLog = true;
    /** /<namespace>:<function> shortcuts for datapack functions. */
    public boolean shortcuts = true;
    /** Permission level for /mcsi (the interpreter). */
    public int interpreterLevel = 2;
    /** Permission level for the debugger (/mcs vars, watch, trace, profile …). */
    public int debugLevel = 2;
    /** Interpreter: maximum loop iterations per input (protects the server from endless loops). */
    public int interpreterMaxSteps = 100_000;

    static Config load(Path dir) {
        Config c = new Config();
        Path file = dir.resolve("mcs-runtime.properties");
        Properties p = new Properties();
        try {
            if (Files.exists(file)) try (Reader r = Files.newBufferedReader(file)) { p.load(r); }
            c.sudoEnabled = bool(p, "sudo.enabled", c.sudoEnabled);
            c.sudoLog = bool(p, "sudo.log", c.sudoLog);
            c.shortcuts = bool(p, "shortcuts.enabled", c.shortcuts);
            c.interpreterLevel = num(p, "interpreter.permission", c.interpreterLevel);
            c.debugLevel = num(p, "debug.permission", c.debugLevel);
            c.interpreterMaxSteps = num(p, "interpreter.max-steps", c.interpreterMaxSteps);
            if (!Files.exists(file)) {
                Files.createDirectories(dir);
                try (Writer w = Files.newBufferedWriter(file)) {
                    w.write("""
                        # MC# runtime (mcs.jar)
                        # Datapack functions may run permission level 3-4 commands (Runtime.Sudo / mcs sudo).
                        # Only function files can use it; players and command blocks cannot.
                        sudo.enabled=%s
                        # Log every sudo command
                        sudo.log=%s
                        # /<namespace>:<function> shortcuts (functions with a "__" path segment are hidden)
                        shortcuts.enabled=%s
                        # Permission level for /mcsi (interpreter) and the debugger (/mcs vars, watch, trace, profile)
                        interpreter.permission=%d
                        debug.permission=%d
                        # Interpreter: maximum loop iterations per input
                        interpreter.max-steps=%d
                        """.formatted(c.sudoEnabled, c.sudoLog, c.shortcuts, c.interpreterLevel, c.debugLevel, c.interpreterMaxSteps));
                }
            }
        } catch (IOException | RuntimeException e) {
            McsRuntime.LOG.warn("Could not read {}: {} (using defaults)", file, e.toString());
        }
        return c;
    }

    private static boolean bool(Properties p, String k, boolean d) {
        String v = p.getProperty(k);
        return v == null ? d : Boolean.parseBoolean(v.trim());
    }

    private static int num(Properties p, String k, int d) {
        String v = p.getProperty(k);
        try { return v == null ? d : Integer.parseInt(v.trim()); } catch (NumberFormatException e) { return d; }
    }
}
