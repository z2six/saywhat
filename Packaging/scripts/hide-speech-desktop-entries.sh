#!/usr/bin/env bash
# The isolated speech component needs no Windows shell/profiler app entries.
# Sourcing this file only defines pure filesystem helpers for offline fixtures.
# Never change another distro, global WSL settings, drivers or inference engines.
set -euo pipefail

saywhat_plain_file() {
    local file=$1 parent
    [[ -f "$file" && ! -L "$file" ]] || return 1
    parent=$(dirname -- "$file")
    while [[ "$parent" != / && "$parent" != . ]]; do
        [[ ! -L "$parent" ]] || return 1
        parent=$(dirname -- "$parent")
    done
    [[ $(stat -c %s -- "$file") -le 65536 ]]
}

saywhat_commit_metadata() {
    local file=$1 temporary=$2 backup="$1.saywhat-original"
    if cmp -s -- "$file" "$temporary"; then rm -- "$temporary"; return; fi
    if [[ ! -e "$backup" && ! -L "$backup" ]]; then cp -p -- "$file" "$backup"; fi
    [[ -f "$backup" && ! -L "$backup" ]] || { rm -- "$temporary"; return 1; }
    chmod --reference="$file" "$temporary"
    chown --reference="$file" "$temporary"
    mv -- "$temporary" "$file"
}

saywhat_hide_distribution_metadata() {
    local file=$1 temporary
    saywhat_plain_file "$file" || return 0
    temporary=$(mktemp "$file.saywhat-new.XXXXXXXX")
    awk '
        function finish() { if ((section == "shortcut" || section == "windowsterminal") && !enabled) print "enabled = false" }
        /^[[:space:]]*\[/ {
            finish(); section = tolower($0); gsub(/[[:space:]\[\]]/, "", section); enabled = 0
            if (section == "shortcut") shortcut = 1
            if (section == "windowsterminal") terminal = 1
        }
        {
            key = tolower($0)
            if ((section == "shortcut" || section == "windowsterminal") && key ~ /^[[:space:]]*enabled[[:space:]]*=/) {
                if (!enabled) print "enabled = false"; enabled = 1; next
            }
            print
        }
        END {
            finish()
            if (!shortcut) print "\n[shortcut]\nenabled = false"
            if (!terminal) print "\n[windowsterminal]\nenabled = false"
        }
    ' "$file" > "$temporary"
    saywhat_commit_metadata "$file" "$temporary"
}

saywhat_hide_profiler_metadata() {
    local applications=$1 file temporary command
    [[ -d "$applications" && ! -L "$applications" ]] || return 0
    shopt -s nullglob
    for file in "$applications"/nsight.desktop "$applications"/nvvp.desktop \
        "$applications"/nsight-compute-[0-9]*.desktop "$applications"/nsys-ui-[0-9]*.desktop; do
        saywhat_plain_file "$file" || continue
        command=$(awk '/^Exec=/{sub(/^Exec=/, ""); print; exit}' "$file")
        # Do not modify user-created launchers that merely share a familiar name.
        [[ "$command" =~ ^/usr/local/cuda-[0-9]+\.[0-9]+/bin/(nsight|nvvp)$ || \
           "$command" =~ ^/opt/nvidia/nsight-compute/[0-9]+\.[0-9]+\.[0-9]+/host/linux-desktop-glibc_[0-9_]+-x64/ncu-ui$ || \
           "$command" =~ ^/opt/nvidia/nsight-systems/[0-9]+\.[0-9]+\.[0-9]+/host-linux-x64/nsys-ui$ ]] || continue
        temporary=$(mktemp "$file.saywhat-new.XXXXXXXX")
        awk '
            function finish() { if (entry) { print "NoDisplay=true"; print "Hidden=true" } }
            /^\[/ { finish(); entry = ($0 == "[Desktop Entry]") }
            { if (entry && $0 ~ /^(NoDisplay|Hidden)=/) next; print }
            END { finish() }
        ' "$file" > "$temporary"
        saywhat_commit_metadata "$file" "$temporary"
    done
}

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
    mode=${1:?ownership mode required}
    name=${WSL_DISTRO_NAME:?WSL distribution name missing}
    [[ $(id -u) == 0 && "$name" =~ ^(SayWhat|LocalTalk)-Ubuntu(-[a-f0-9]{8})?$ ]] || {
        echo 'Desktop integration changes are limited to the isolated app-owned speech component.' >&2; exit 2;
    }
    case "$mode" in
        --owned-setup) ;; # Called only immediately after automatic setup creates its isolated distro.
        --owned-runtime)
            server=${2:?registered server path required}
            [[ "$server" =~ ^/opt/localtalk-voxtral/[a-f0-9]{40}-localtalk-r[0-9]+/build-(cuda|vulkan|cpu)/voxtral-server$ && -x "$server" && ! -L "$server" ]] || exit 3
            source_root=$(dirname -- "$(dirname -- "$server")")
            revision=${source_root##*-}
            saywhat_plain_file "$source_root/.localtalk-source-verified-localtalk-$revision" || exit 3
            ;;
        *) exit 2 ;;
    esac
    saywhat_hide_distribution_metadata /etc/wsl-distribution.conf
    saywhat_hide_profiler_metadata /usr/share/applications
    echo 'Internal speech desktop entries are hidden; recoverable originals are retained. No model was loaded.'
fi
