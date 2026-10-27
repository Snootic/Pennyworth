#!/usr/bin/env bash
# Starts a packaged Pennyworth in a private, headless display with an empty environment and proves it opened a window
# and drew something.
#
# Usage: tools/package/smoke-linux.sh (--tarball FILE | --appimage FILE | --appdir DIR) [--display x11|wayland]
#                                      [--shot OUT.png] [--seconds N]
#
#   x11      (default) a private Xvfb. Needs Xvfb, ImageMagick (import, identify) and python3.
#            Also checks that the window class equals StartupWMClass in the shipped desktop file.
#   wayland  a private headless sway (wlroots, pixman renderer) with no X server at all, so the window can only come
#            from Avalonia's native Wayland backend. Needs sway, grim, ImageMagick and python3.
#            Also checks that the window's app_id is the desktop file's name.
#
# The app runs under `env -i` with an empty PATH directory: no SDK, no DOTNET_ROOT, nothing from the calling shell.
# It never touches the display the script was started from. Everything it starts is stopped on exit.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
kind="" subject="" display=x11 shot="" seconds=15

while [ $# -gt 0 ]; do
    case "$1" in
        --tarball|--appimage|--appdir) kind="${1#--}"; subject="$(realpath "${2:?$1 needs a path}")"; shift 2 ;;
        --display) display="${2:?--display needs x11 or wayland}"; shift 2 ;;
        --shot) shot="$(realpath -m "${2:?--shot needs a path}")"; shift 2 ;;
        --seconds) seconds="${2:?--seconds needs a number}"; shift 2 ;;
        -h|--help) sed -n '2,/^set -euo/p' "$0" | sed '$d' | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) echo "unknown option: $1" >&2; exit 2 ;;
    esac
done
[ -n "$kind" ] || { echo "say what to run: --tarball, --appimage or --appdir" >&2; exit 2; }
case "$display" in x11|wayland) ;; *) echo "--display is x11 or wayland" >&2; exit 2 ;; esac

fail() { echo "SMOKE FAILED: $*" >&2; [ -f "${log:-/nonexistent}" ] && { echo "--- application output ---" >&2; cat "$log" >&2; }; exit 1; }

work="$(mktemp -d)"
log="$work/app.log"
pids=()
cleanup() {
    for pid in "${pids[@]}"; do kill "$pid" 2>/dev/null || true; done
    for pid in "${pids[@]}"; do wait "$pid" 2>/dev/null || true; done
    rm -rf "$work"
}
trap cleanup EXIT

# --- what to run ---------------------------------------------------------------------------------------------------
extra_env=()
case "$kind" in
    tarball)
        mkdir "$work/app" && tar -xzf "$subject" -C "$work/app"
        command=("$(echo "$work"/app/*/bin/pennyworth)")
        desktop="$(echo "$work"/app/*/share/applications/pennyworth.desktop)" ;;
    appdir)
        command=("$subject/AppRun")
        desktop="$subject/pennyworth.desktop" ;;
    appimage)
        command=("$subject")
        extra_env=(APPIMAGE_EXTRACT_AND_RUN=1)   # no FUSE needed, which is also the case in most CI containers
        desktop="" ;;
esac
[ -x "${command[0]}" ] || fail "${command[0]} is not an executable"
wm_class="Pennyworth"
if [ -n "$desktop" ]; then
    wm_class="$(sed -n 's/^StartupWMClass=//p' "$desktop")"
    [ -n "$wm_class" ] || fail "$desktop has no StartupWMClass"
fi

mkdir "$work/home" "$work/nobin" "$work/run"
chmod 700 "$work/run"
app_env=(HOME="$work/home" PATH="$work/nobin" LANG=C.UTF-8 XDG_RUNTIME_DIR="$work/run" "${extra_env[@]}")

# --- a private display ----------------------------------------------------------------------------------------------
if [ "$display" = x11 ]; then
    command -v Xvfb >/dev/null || fail "Xvfb is not installed"
    : > "$work/display"
    Xvfb -displayfd 3 -screen 0 1400x900x24 -ac -nolisten tcp 3> "$work/display" >"$work/xvfb.log" 2>&1 &
    pids+=($!)
    for _ in $(seq 1 50); do [ -s "$work/display" ] && break; sleep 0.1; done
    [ -s "$work/display" ] || fail "Xvfb did not start: $(cat "$work/xvfb.log")"
    x_display=":$(head -n1 "$work/display")"
    app_env+=(DISPLAY="$x_display")
    screenshot() { DISPLAY="$x_display" import -window root "$1"; }
    window_class() { DISPLAY="$x_display" python3 "$here/linux/x11-wmclass.py" 2>/dev/null || true; }
else
    command -v sway >/dev/null && command -v grim >/dev/null || fail "sway and grim are not installed"
    printf 'output HEADLESS-1 resolution 1400x900\n' > "$work/sway.conf"
    env -i PATH=/usr/bin:/bin XDG_RUNTIME_DIR="$work/run" WLR_BACKENDS=headless WLR_LIBINPUT_NO_DEVICES=1 WLR_RENDERER=pixman \
        sway --config "$work/sway.conf" >"$work/sway.log" 2>&1 &
    pids+=($!)
    for _ in $(seq 1 100); do ls "$work"/run/wayland-* >/dev/null 2>&1 && ls "$work"/run/sway-ipc.*.sock >/dev/null 2>&1 && break; sleep 0.1; done
    socket="$(ls "$work"/run/wayland-* 2>/dev/null | grep -v '\.lock$' | head -n1 || true)"
    [ -n "$socket" ] || fail "sway did not start: $(cat "$work/sway.log")"
    app_env+=(WAYLAND_DISPLAY="$(basename "$socket")")
    sway_ipc="$(ls "$work"/run/sway-ipc.*.sock | head -n1)"
    screenshot() { env -i XDG_RUNTIME_DIR="$work/run" WAYLAND_DISPLAY="$(basename "$socket")" grim "$1"; }
    window_class() {
        SWAYSOCK="$sway_ipc" swaymsg -t get_tree 2>/dev/null \
            | python3 -c 'import json,sys
def walk(n):
    if n.get("app_id"): print(n["app_id"])
    for c in n.get("nodes", []) + n.get("floating_nodes", []): walk(c)
walk(json.load(sys.stdin))' || true
    }
    wm_class="pennyworth"   # xdg_toplevel app_id = the desktop file's name
fi

# --- run it ---------------------------------------------------------------------------------------------------------
command -v dotnet >/dev/null 2>&1 && echo "note: dotnet is on this shell's PATH, but the app runs with PATH=$work/nobin"
env -i "${app_env[@]}" "${command[@]}" >"$log" 2>&1 &
app=$!
pids+=("$app")

deadline=$((SECONDS + seconds))
found=false
while [ $SECONDS -lt $deadline ]; do
    kill -0 "$app" 2>/dev/null || fail "Pennyworth exited before opening a window"
    if window_class | grep -qx "$wm_class"; then found=true; break; fi
    sleep 0.5
done
$found || fail "no window with class/app_id '$wm_class' within ${seconds}s (saw: $(window_class | tr '\n' ' '))"

sleep 3   # let it paint the first frames and run its first live-numbers tick
kill -0 "$app" 2>/dev/null || fail "Pennyworth exited right after opening its window"
png="${shot:-$work/shot.png}"
mkdir -p "$(dirname "$png")"
screenshot "$png"
colors="$(identify -format '%k' "$png")"
[ "$colors" -ge 500 ] || fail "the screenshot is nearly blank ($colors colours)"
grep -q -i 'unhandled exception' "$log" && fail "the application printed an unhandled exception"
kill -0 "$app" 2>/dev/null || fail "Pennyworth died while taking the screenshot"

echo "SMOKE OK ($kind, $display): window '$wm_class' opened, $(identify -format '%wx%h' "$png") screenshot with $colors colours${shot:+, saved to $shot}"
if [ -s "$log" ]; then echo "--- application output ---"; cat "$log"; fi
