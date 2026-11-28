package mcs.runtime;

import java.nio.file.Path;

/** What a loader (Fabric, NeoForge, Paper) tells the shared runtime. */
public interface Platform {
    /** "Fabric", "NeoForge" or "Paper" */
    String name();

    /** Folder for mcs-runtime.properties */
    Path configDir();
}
