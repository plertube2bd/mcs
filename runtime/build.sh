#!/bin/sh
# Builds mcs.jar (the MC# runtime) for Fabric, NeoForge and Paper into runtime/dist/.
# Each jar works on Minecraft 26.1 - 26.3. Needs Java 25 and Gradle 9 (internet access the first time).
#   runtime/build.sh [fabric] [neoforge] [paper]
set -e
cd "$(dirname "$0")"
loaders="${*:-fabric neoforge paper}"
mkdir -p dist
for l in $loaders; do
    echo "== $l"
    (cd "$l" && gradle build --no-daemon -q)
    cp "$l"/build/libs/mcs-"$l"-*.jar "dist/mcs-$l.jar"
done
ls -l dist
