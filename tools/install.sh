#!/usr/bin/env bash
# Installs the MC# compiler as the `mcs` command (run again to update).
#
#   tools/install.sh                 # → ~/.local/share/mcs + ~/.local/bin/mcs
#   PREFIX=/usr/local tools/install.sh
#   tools/install.sh --uninstall
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PREFIX="${PREFIX:-$HOME/.local}"
LIB="$PREFIX/share/mcs"
BIN="$PREFIX/bin"

if [ "${1:-}" = "--uninstall" ]; then
    rm -rf "$LIB" "$BIN/mcs"
    echo "removed $LIB and $BIN/mcs"
    exit 0
fi

export PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
command -v dotnet >/dev/null || { echo "dotnet (the .NET 8 SDK) is needed: https://dot.net" >&2; exit 1; }

# A separate published copy: rebuilding the repository does not touch the installed compiler
rm -rf "$LIB.new"
dotnet publish "$ROOT/src/Mcs.Compiler" -c Release -o "$LIB.new" -v q -nologo
rm -rf "$LIB" && mv "$LIB.new" "$LIB"

mkdir -p "$BIN"
cat > "$BIN/mcs" <<EOF
#!/usr/bin/env bash
# MC# compiler (installed from $ROOT)
DOTNET="\$HOME/.dotnet/dotnet"; [ -x "\$DOTNET" ] || DOTNET=dotnet
DOTNET_CLI_TELEMETRY_OPTOUT=1 exec "\$DOTNET" "$LIB/mcs.dll" "\$@"
EOF
chmod +x "$BIN/mcs"
echo "installed: $BIN/mcs → $LIB"

case ":$PATH:" in
    *":$BIN:"*) ;;
    *) echo "note: $BIN is not on your PATH (fish: fish_add_path $BIN; bash/zsh: export PATH=\"$BIN:\$PATH\")" ;;
esac
