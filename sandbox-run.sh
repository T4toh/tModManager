#!/usr/bin/env bash
# Runs tModManager with the whole disk read-only except its own data, the CP2077 game dir and its Proton prefix.
# A delete that escapes those paths gets EROFS instead of eating $HOME (see the 2026-09-25 prefix incident in CLAUDE.md).
# ~/.local is one writable mount (so renames between game dir, Backups and Temp stay on one mount, no EXDEV);
# every sibling of the kept paths is ro-bound over it.
#
#   ./sandbox-run.sh           launch the Release build inside the jail
#   ./sandbox-run.sh --check   prove the jail: kept paths writable, everything else not (exit 1 otherwise)
#
# Steam stays outside: launching the game from the app does not work in here. protontricks does (REDmod deploy,
# "Instalar" in the Wine prefix panel): its cache and the Steam Linux Runtime dirs are writable, see below.
set -euo pipefail
command -v bwrap >/dev/null || { echo "Falta bubblewrap: sudo pacman -S bubblewrap"; exit 1; }

L=$HOME/.local
STEAM=$L/share/Steam/steamapps
KEEP=(
  "$L/share/tModManager"
  "$L/state/tModManager"
  "$STEAM/common/Cyberpunk 2077"
  "$STEAM/compatdata/1091500"
)
# protontricks-launch (REDmod deploy) runs the Steam Linux Runtime, which locks <runtime>/files/.ref and
# writes temp dirs under <runtime>/var. Steam reinstalls these on its own, so they get in whole.
for d in "$STEAM"/common/SteamLinuxRuntime*; do KEEP+=("$d"); done
APP=${APP:-$(dirname "$(realpath "$0")")/src/NexusMods.App/bin/Release/net10.0/tModManager}

is_keep() { local k; for k in "${KEEP[@]}"; do [[ $1 == "$k" ]] && return 0; done; return 1; }
is_ancestor() { local k; for k in "${KEEP[@]}"; do [[ $k == "$1"/* ]] && return 0; done; return 1; }

args=(--ro-bind / / --dev-bind /dev /dev --proc /proc --bind /tmp /tmp
      --bind "/run/user/$UID" "/run/user/$UID" --bind "$L" "$L")
# protontricks rebuilds ~/.cache/protontricks/proton/<proton>/bin on every run. Outside ~/.local, so bound by hand.
mkdir -p "$HOME/.cache/protontricks"
args+=(--bind "$HOME/.cache/protontricks" "$HOME/.cache/protontricks")
KEEP+=("$HOME/.cache/protontricks")   # only so --check probes it
protect() {
  local c
  shopt -s nullglob dotglob
  for c in "$1"/*; do
    [[ -L $c ]] && continue   # a link inside ~/.local points either into a protected tree or outside (already ro)
    is_keep "$c" && continue
    if is_ancestor "$c"; then protect "$c"; else args+=(--ro-bind "$c" "$c"); fi
  done
}
protect "$L"

if [[ ${1:-} == --check ]]; then
  # Ancestors of the kept paths (~/.local/share, steamapps, ...) only accept new entries: everything already
  # inside them is ro-bound, so they are not probed here.
  exec bwrap "${args[@]}" bash -c '
    bad=0
    for d in "$@"; do
      t="$d/.sandbox-probe"
      if touch "$t" 2>/dev/null; then rm "$t"; echo "rw  $d"; else echo "FAIL tendria que ser escribible: $d"; bad=1; fi
    done
    for d in "$HOME" "$HOME/.config" "$HOME/.local/share/kwalletd" "$HOME/.local/share/Steam/config" \
             "$HOME/.local/share/Steam/steamapps/compatdata/1091500/pfx/dosdevices/z:$HOME"; do
      [[ -d $d ]] || continue
      if touch "$d/.sandbox-probe" 2>/dev/null; then rm "$d/.sandbox-probe"; echo "FAIL escribible: $d"; bad=1; else echo "ro  $d"; fi
    done
    exit $bad' _ "${KEEP[@]}"
fi

[[ -x $APP ]] || { echo "No existe $APP: compilar con dotnet build -c Release"; exit 1; }
export DOTNET_ROOT=${DOTNET_ROOT:-$HOME/.dotnet}
exec bwrap "${args[@]}" "$APP" "$@"
