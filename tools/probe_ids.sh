#!/usr/bin/env bash
# Prints the server's responses to a parse probe of every block/item/entity ID (used to find feature-flagged IDs).
# usage: probe_ids.sh <server dir with eula.txt accepted> <server jar> <java> <reports dir>
# then: grep "<--\[HERE\]" output | grep -oP "minecraft:[a-z0-9_]+" | sed s/minecraft:// | sort -u > <gen>/<ver>/disabled.txt
set -u
D=$1; JAR=$2; JAVA=$3; REP=$4
cd "$D"; grep -q 'eula=true' eula.txt || exit 2
rm -rf world logs in log.txt
printf 'online-mode=false\nlevel-type=minecraft\\:flat\nserver-port=25599\n' > server.properties
python3 -I - "$REP" > probe_cmds.txt <<'PY'
import json, sys
rep = sys.argv[1]
reg = json.load(open(rep + "/registries.json"))
for b in json.load(open(rep + "/blocks.json")): print(f"execute if block 0 -64 0 {b}")
for i in reg["minecraft:item"]["entries"]: print(f"clear @a {i} 0")
for e in reg["minecraft:entity_type"]["entries"]: print(f"execute if entity @e[type={e}]")
PY
mkfifo in
"$JAVA" -Xmx2G -jar "$JAR" nogui < in > log.txt 2>&1 &
PID=$!
exec 3>in
for i in $(seq 1 180); do grep -q 'Done (' log.txt && break; sleep 1; done
sleep 2
cat probe_cmds.txt >&3
sleep 8
echo stop >&3
wait $PID
grep -E 'Server thread/INFO\]: ' log.txt | sed -E 's/^\[[0-9:]+\] \[Server thread\/INFO\]: //' | grep -vE '^(Test (passed|failed)|No player was found|Stopping|Saving|ThreadedAnvil|Done|Preparing|Time elapsed|Starting|Loading|Default|Generating|Using)' 
