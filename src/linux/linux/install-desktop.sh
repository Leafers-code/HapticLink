#!/bin/sh
set -eu
app_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
desktop_dir="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
mkdir -p "$desktop_dir"
# Desktop entry escaping, including paths containing spaces and percent signs.
escaped=$(printf '%s' "$app_dir/HapticLink" | sed 's/\\/\\\\/g; s/"/\\"/g; s/`/\\`/g; s/\$/\\$/g; s/%/%%/g')
cat > "$desktop_dir/hapticlink.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=HapticLink
Comment=Game audio, controller rumble and OSC haptics
Exec="$escaped"
Icon=input-gaming
Terminal=false
Categories=Utility;Game;
EOF
printf '%s\n' "Desktop shortcut installed for this user."
