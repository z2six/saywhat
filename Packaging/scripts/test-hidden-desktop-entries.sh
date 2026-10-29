#!/usr/bin/env bash
# Retained synthetic workspace fixtures only; no system metadata or model access.
set -euo pipefail
fixture=${1:?absolute fixture directory required}
[[ "$fixture" == /mnt/[a-z]/*/offline-speech-desktop-fixtures-* && ! -e "$fixture" && ! -L "$fixture" ]] || exit 2
source "$(dirname "$0")/hide-speech-desktop-entries.sh"
mkdir -p -- "$fixture/applications" "$fixture/linked"
checks=0
require() { if ! "$@"; then echo "Desktop metadata fixture check failed: $*" >&2; exit 1; fi; checks=$((checks + 1)); }
config="$fixture/wsl-distribution.conf"
printf '%s\n' '[oobe]' 'command = /usr/lib/wsl/wsl-setup' 'defaultUid = 1000' '[shortcut]' 'icon = /usr/share/wsl/ubuntu.ico' 'enabled = true' '[windowsterminal]' 'enabled=true' 'ProfileTemplate = /usr/share/wsl/terminal-profile.json' '[unrelated]' 'enabled = true' > "$config"
saywhat_hide_distribution_metadata "$config"
require test "$(grep -c '^enabled = false$' "$config")" -eq 2
require grep -Fq 'command = /usr/lib/wsl/wsl-setup' "$config"
require grep -Fq 'icon = /usr/share/wsl/ubuntu.ico' "$config"
require grep -Fq 'ProfileTemplate = /usr/share/wsl/terminal-profile.json' "$config"
require grep -Fq 'enabled = true' "$config"
require grep -Fq 'enabled = true' "$config.saywhat-original"
hash=$(sha256sum "$config")
saywhat_hide_distribution_metadata "$config"
require test "$hash" = "$(sha256sum "$config")"

profiler="$fixture/applications/nvvp.desktop"
printf '%s\n' '[Desktop Entry]' 'Name=NVIDIA Visual Profiler' 'Exec=/usr/local/cuda-13.1/bin/nvvp' 'NoDisplay=false' 'Hidden=false' '[Desktop Action Other]' 'Hidden=false' > "$profiler"
printf '%s\n' '[Desktop Entry]' 'Exec=/user/other-program' > "$fixture/applications/nsight.desktop"
printf '%s\n' '[Desktop Entry]' 'Exec=/usr/local/cuda-13.1/bin/nvvp' > "$fixture/user-file"
ln -s -- "$fixture/user-file" "$fixture/applications/nsys-ui-2025.5.2.desktop"
saywhat_hide_profiler_metadata "$fixture/applications"
require grep -Fxq 'NoDisplay=true' "$profiler"
require grep -Fxq 'Hidden=true' "$profiler"
require grep -Fxq 'Hidden=false' "$profiler"
require grep -Fxq 'Exec=/usr/local/cuda-13.1/bin/nvvp' "$profiler"
require grep -Fxq 'NoDisplay=false' "$profiler.saywhat-original"
require test "$(grep -c 'NoDisplay' "$fixture/applications/nsight.desktop" || true)" -eq 0
require test "$(grep -c 'NoDisplay' "$fixture/user-file" || true)" -eq 0
hash=$(sha256sum "$profiler")
saywhat_hide_profiler_metadata "$fixture/applications"
require test "$hash" = "$(sha256sum "$profiler")"

printf '%s\n' '[oobe]' 'command=keep' > "$fixture/no-sections.conf"
saywhat_hide_distribution_metadata "$fixture/no-sections.conf"
require test "$(grep -c '^enabled = false$' "$fixture/no-sections.conf")" -eq 2
ln -s -- "$fixture" "$fixture/linked/redirect"
saywhat_hide_distribution_metadata "$fixture/linked/redirect/no-sections.conf"
require test ! -e "$fixture/linked/redirect/no-sections.conf.saywhat-new"
require test "$(find "$fixture" -name '*.saywhat-new.*' | wc -l)" -eq 0
echo "PASS $checks desktop metadata checks. Fixtures retained at $fixture. No real WSL config, apps or models changed."
