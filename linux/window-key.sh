#!/usr/bin/env bash
# Send a limited set of keys to exactly one visible X11 window owned by PID.
# xdotool is used ONLY for discovery. Delivery uses XSendEvent directly, never
# XTEST, focus manipulation or seat-global input. Assert receipt in app logs.
set -u
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
if [ "$#" -ne 3 ] || ! [[ "$1" =~ ^[1-9][0-9]*$ ]] || [ ! -f "$3" ]; then
    echo "usage: $0 <app-pid> <F9|F10|F12|Escape> <app-key-log>" >&2
    exit 2
fi
PID="$1"; KEY="$2"; LOG="$3"
case "$KEY" in F9|F10|F12|Escape) ;; *) echo "error: unsupported key" >&2; exit 2 ;; esac
XD="$(command -v xdotool || true)"
if [ -n "$XD" ]; then
    xdo() { "$XD" "$@"; }
else
    XD="$ROOT/.tools/xdotoolroot/usr/bin/xdotool"
    xdo() { LD_LIBRARY_PATH="$ROOT/.tools/xdotoolroot/usr/lib64${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}" "$XD" "$@"; }
fi
if [ ! -x "$XD" ] || [ -z "${DISPLAY:-}" ] || ! command -v cc >/dev/null; then
    echo "error: X display, xdotool and C compiler are required" >&2
    exit 2
fi
mapfile -t WINS < <(xdo search --onlyvisible --pid "$PID" 2>/dev/null || :)
if [ "${#WINS[@]}" -ne 1 ] || ! [[ "${WINS[0]:-}" =~ ^[1-9][0-9]*$ ]]; then
    echo "error: expected exactly one visible X11 window for pid $PID; found ${#WINS[@]}" >&2
    exit 2
fi
TMP="$(mktemp -d)" || exit 2
trap 'rm -rf -- "$TMP"' EXIT
if ! cc -std=c11 -Wall -Wextra -Werror "$ROOT/linux/window-send-key.c" -lX11 -o "$TMP/window-send-key"; then
    echo "error: could not build XSendEvent helper (X11 development files required)" >&2
    exit 2
fi
before_down=$(grep -c "^key: $KEY code:" "$LOG" || :)
before_up=$(grep -c "^key up: $KEY code:" "$LOG" || :)
"$TMP/window-send-key" "${WINS[0]}" "$PID" "$KEY" down || exit 2
# Do not release until the app has processed its press: GLFW can duplicate an
# XSendEvent pair delivered in one poll. Every call requires app log ACKs.
for _ in {1..300}; do
    [ "$(grep -c "^key: $KEY code:" "$LOG" || :)" -gt "$before_down" ] && break
    sleep 0.1
done
if [ "$(grep -c "^key: $KEY code:" "$LOG" || :)" -le "$before_down" ]; then
    echo "error: key press not acknowledged" >&2
    # Best effort release only on the same validated target; never fall back.
    "$TMP/window-send-key" "${WINS[0]}" "$PID" "$KEY" up 2>/dev/null || :
    exit 2
fi
if ! "$TMP/window-send-key" "${WINS[0]}" "$PID" "$KEY" up 2>"$TMP/release-error"; then
    # Escape may have already closed the target after the acknowledged press.
    # Esc may destroy its window after the press ACK. Check the original
    # window really vanished; other helper failures must not count as success.
    if [ "$KEY" = Escape ] && ! xdo getwindowname "${WINS[0]}" >/dev/null 2>&1; then
        exit 0
    fi
    cat "$TMP/release-error" >&2
    exit 2
fi
for _ in {1..300}; do
    [ "$(grep -c "^key up: $KEY code:" "$LOG" || :)" -gt "$before_up" ] && break
    if [ "$KEY" = Escape ] && ! xdo getwindowname "${WINS[0]}" >/dev/null 2>&1; then exit 0; fi
    sleep 0.1
done
if [ "$(grep -c "^key up: $KEY code:" "$LOG" || :)" -le "$before_up" ]; then
    echo "error: key release not acknowledged" >&2
    exit 2
fi
echo "window-key: pid $PID window ${WINS[0]} <- $KEY (acknowledged)"
