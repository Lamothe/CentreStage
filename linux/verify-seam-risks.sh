#!/bin/bash
# verify-seam-risks.sh — behavioural verification of the DLSS seam risk paths
# that code review could only build-verify:
#
#   A. interactive F9 Trigger wiring + the worker/presenter path + Esc close
#      (the camera-drag Release is NOT injectable on this session — uinput
#      mouse events never reach the window — and is reported as a known FAIL
#      with a manual recipe in the README)
#   B. mid-flight close/Esc gating: the Esc is handled while a frame is in
#      flight (asserted on the app's "close deferred" line preceding the
#      completion line), the app keeps painting, completes the frame and
#      exits rc 0
#   C. worker-fault crash path + retry from Failed (fault injection via the
#      test-only CENTRESTAGE_DLSS_FAULT=throw-once hook)
#
# Input injection: ydotool (uinput, evdev-level — works for the app whatever
# windowing backend it uses). ydotoold is started deterministically for the
# run (a stale socket file from a dead daemon silently breaks every
# injection). Keys are evdev codes: ESC=1, F9=67 (F11=87), F12=88. Mouse drag
# = click 0x40 (down) / 0x80 (up); a plain click is 0xC0 (0x40 is down-only).
#
# Focus: this GNOME Wayland session ignores focus-follows-mouse (the
# focus-mode setting is legacy X11) and only click-focus works, while the
# launch-focus grant is unreliable. Every key that must land is therefore
# preceded by a click on the app window (harmless in this viewer: a click
# without a drag does not touch the camera), and the window is discovered by
# a click+F12 probe sweep. Every injected key that must land is asserted via
# its effect on the app log (the F9 request line, the mid-flight deferral
# line, the screenshot line or the clean rc-0 exit); the F12 captures are
# additionally checked for real (non-blank) content.
#
# Unattended: no dialogs, no interaction. The session's idle-delay is set to 0
# for the run (restored on exit) so GNOME does not blank and throttle the
# window's render cadence. The pointer is parked on the secondary display
# before each launch (Mutter places new windows on the pointer's monitor;
# Wayland ignores position hints, so placement is best-effort and the window's
# geometry cannot be queried — screenshots are of the app's framebuffer).
#
# Requirements: built CentreStage (dotnet build), a graphical session, and the
# ydotool+ydotoold binaries in .tools/ydotoolroot (extracted from the Fedora
# ydotool RPM; /dev/uinput must grant the user rw). Evidence (logs +
# screenshots) is written to the output directory; NOTHING here enters Git.
#
# CENTRESTAGE_RISK_ONLY=A|B|C runs one scenario (each scenario starts its own
# app instance; a single invocation runs A, then B, then C).
set -u

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
# The demo subject (the earlier pixel-girl assets were deleted; the suite
# now uses Michael's chosen test asset, same as the evidence runs).
MODEL="${CENTRESTAGE_RISK_MODEL:-/home/michael/Projects/UniRig/work/Teen_Kitty/Teen_Kitty_rigged.glb}"
OUT="${1:-$ROOT/.evidence/seam-risks-$(date +%H%M%S)}"
ONLY="${CENTRESTAGE_RISK_ONLY:-all}"
YD="$ROOT/.tools/ydotoolroot/usr/bin"
PASS=0
FAIL=0

if [ ! -x "$YD/ydotool" ] || [ ! -x "$YD/ydotoold" ]; then
    echo "error: ydotool not found — extract the Fedora ydotool RPM into .tools/ydotoolroot" >&2
    exit 2
fi
if [ ! -w /dev/uinput ]; then
    echo "error: /dev/uinput is not writable for $(id -un); input injection unavailable" >&2
    exit 2
fi
if ! python3 -c "import PIL" 2>/dev/null; then
    echo "error: python3-Pillow is required (the capture content check)" >&2
    exit 2
fi

mkdir -p "$OUT"

# Pre-flight: no other viewer may hold keyboard focus (a stale instance
# silently steals every injected key).
pkill -f '[C]entreStage/bin' 2>/dev/null
sleep 1

ORIG_IDLE="$(gsettings get org.gnome.desktop.session idle-delay)"
gsettings set org.gnome.desktop.session idle-delay 0
restore() {
    gsettings set org.gnome.desktop.session idle-delay "$ORIG_IDLE"
    pkill -f '[C]entreStage/bin' 2>/dev/null
    pkill -f '[y]dotoold' 2>/dev/null
}
trap restore EXIT

# Start the input daemon deterministically (see header).
start_daemon() {
    pkill -f '[y]dotoold' 2>/dev/null
    rm -f "$XDG_RUNTIME_DIR/.ydotool_socket"
    "$YD/ydotoold" --socket-path "$XDG_RUNTIME_DIR/.ydotool_socket" \
        >> "$OUT/ydotoold.log" 2>&1 < /dev/null &
    sleep 2
}
start_daemon

# yd() — run ydotool, restarting the daemon and retrying when its socket
# refuses connections (the socket flaps under the evaluator's CPU burn, so a
# silent send would be a lost event).
yd() {
    local out i
    for i in 1 2 3; do
        out=$("$YD/ydotool" "$@" 2>&1)
        case "$out" in
            *"failed to connect"*|*"Connection refused"*)
                start_daemon
                continue ;;
            *)
                return 0 ;;
        esac
    done
    echo "WARN: ydotool $* failed: $out"
    return 1
}

say() { echo "== $*"; }
check() {
    local label="$1"; shift
    if "$@"; then
        echo "PASS: $label"; PASS=$((PASS + 1))
    else
        echo "FAIL: $label"; FAIL=$((FAIL + 1))
    fi
}

wait_log() { # wait_log <file> <pattern> <timeout-s>
    local file="$1" pattern="$2" timeout="${3:-60}" i=0
    while [ "$i" -lt "$timeout" ]; do
        grep -q "$pattern" "$file" 2>/dev/null && return 0
        sleep 1
        i=$((i + 1))
    done
    return 1
}

shots_in() { grep -c "screenshot:" "$1" 2>/dev/null || true; }

shot_has_content() { # shot_has_content <log-file> — the newest F12 capture
    # holds real content (a blank/uniform capture is a broken key path even
    # when the PNG exists).
    local shot colors
    shot=$(grep "screenshot:" "$1" | tail -1 | sed "s/.*screenshot: //")
    [ -n "$shot" ] || return 1
    colors=$(python3 -c "from PIL import Image; im = Image.open('$shot').convert('RGB'); print(len(set(im.getdata())))")
    [ "${colors:-0}" -gt 16 ]
}

wait_new_shot() { # wait_new_shot <log-file> <before-count> <timeout-s>
    local log="$1" before="$2" timeout="${3:-15}" i=0
    while [ "$i" -lt "$timeout" ]; do
        [ "$(shots_in "$log")" -gt "$before" ] && return 0
        sleep 1
        i=$((i + 1))
    done
    return 1
}

send_key() { # send_key <evdev-code> — ydotoold 1.0.4 silently dries up after
    # the first key event (proved by a no-evaluation repeat probe), so every
    # key that must land gets a fresh daemon + uinput device.
    start_daemon
    yd key --key-delay 150 "$1"
}

click_focus() { # click the app window (click-focus session); a click without
    # a drag has no camera effect. Fresh daemon per click (see send_key).
    if [ "$FOCUS_X" -gt 0 ]; then
        yd mousemove --absolute -x "$FOCUS_X" -y "$FOCUS_Y"
        sleep 0.2
    fi
    start_daemon
    yd click 0xC0
    sleep 0.3
}

# Where the app window was located (set by grab_focus; used to keep the
# pointer — and with it keyboard focus — inside the window).
FOCUS_X=0
FOCUS_Y=0

launch() { # launch <log-file> [env VAR=...]
    local log="$1"; shift
    # Wake preamble: the first scenario after a long idle has repeatedly
    # lost all input (events never reach the window). Poke the session,
    # then park on the secondary display.
    start_daemon
    yd key --key-delay 100 42:1 42:0          # a harmless Shift tap
    sleep 2
    yd mousemove --absolute -x 4400 -y 540   # park on the secondary display
    ( cd "$ROOT" &&
      env CENTRESTAGE_WINDOW_POS=3480,120 CENTRESTAGE_LOG_KEYS=1 \
          CENTRESTAGE_SHOT_DIR="$OUT" "$@" \
          CENTRESTAGE_AUTO_CLOSE_MS=600000 \
          dotnet run --project src/CentreStage -- "$MODEL" \
          > "$log" 2>&1 < /dev/null ) &
    APP_PID=$!
    wait_log "$log" "gl: primitives" 60 || echo "WARN: load line not seen in $log"
    sleep 1
}

grab_focus() { # grab_focus <log-file> — F12 probe sweep; sets FOCUS_X/Y.
    local log="$1" before after x y attempt
  for attempt in 1 2; do
    sleep 12                                   # the proven-delivery window after launch
    before=$(shots_in "$log")
    # Click + F12 probe: the click gives the window under the pointer keyboard
    # focus (click-focus session); the landed screenshot proves delivery, and
    # the sweep discovers where the compositor placed the window.
    for y in 540 300 700 1100; do
        for x in 4400 400 1100 1800 2500 3200 3900 4600; do
            yd mousemove --absolute -x "$x" -y "$y"
            sleep 0.3
            start_daemon
            yd click 0xC0
            sleep 0.3
            send_key 88
            sleep 0.6
            after=$(shots_in "$log")
            if [ "$after" -gt "$before" ]; then
                FOCUS_X=$x
                FOCUS_Y=$y
                return 0
            fi
        done
    done
    start_daemon                              # second attempt: fresh daemon
  done
    return 1
}

f12_shoot() { # f12_shoot <log-file> — screenshot with a focus click + patient
    # retry (key events can be handled seconds late while the evaluation's CPU
    # burn starves the render loop's event pump, so poll, do not assume).
    local log="$1" before i
    before=$(shots_in "$log")
    send_key 88
    for i in $(seq 1 12); do
        [ "$(shots_in "$log")" -gt "$before" ] && return 0
        sleep 1
    done
    click_focus
    send_key 88
    for i in $(seq 1 12); do
        [ "$(shots_in "$log")" -gt "$before" ] && return 0
        sleep 1
    done
    return 1
}

re_focus() { click_focus; }  # keys that must land are preceded by a click.

close_app() { # close_app <log-file> — a clean exit means rc 0; never inject
    # into an app that already exited (the keys would go to whatever window
    # has focus next).
    if kill -0 "$APP_PID" 2>/dev/null; then
        re_focus
        send_key 1                                 # ESC
    fi
    local i=0 rc
    while [ "$i" -lt 30 ] && kill -0 "$APP_PID" 2>/dev/null; do sleep 1; i=$((i + 1)); done
    if kill -0 "$APP_PID" 2>/dev/null; then
        echo "WARN: app did not exit after Escape; killing"
        pkill -f '[C]entreStage/bin' 2>/dev/null
        sleep 1
        return 1
    fi
    wait "$APP_PID" 2>/dev/null
    rc=$?
    if [ "$rc" -ne 0 ]; then
        echo "WARN: app exited with code $rc"
        return 1
    fi
    return 0
}

run_A() {
    say "scenario A: F9 Trigger + camera drag Release (real input events)"
    local ALOG="$OUT/A-interactive.log"
    launch "$ALOG"
    check "A: the app received input (F12 self-screenshot)" grab_focus "$ALOG"
    re_focus
    send_key 67                                    # F9
    check "A: F9 reached the seam through the key wiring" \
        wait_log "$ALOG" "dlss: F9 requested one frame" 10
    check "A: the frame was processed (worker + presenter)" \
        wait_log "$ALOG" "dlss: frame 1 processed" 90
    check "A: the presented frame was screenshot (F12 key path)" f12_shoot "$ALOG"
    check "A: the captured frame has real content (non-blank)" shot_has_content "$ALOG"
    sleep 3                                                # let the event pump catch up
    click_focus
    # The whole drag goes through ONE ydotool invocation (one daemon
    # connection: the first connection is the reliable one).
    start_daemon
    yd click 0x40 mousemove -x -40 -y -20 \
        mousemove -x 40 -y 20 click 0x80
    check "A: the camera drag released the presented result (mouse wiring)" \
        wait_log "$ALOG" "dlss: camera input released the presented result" 12
    check "A: clean exit after Escape" close_app "$ALOG"
}

run_B() {
    say "scenario B: Escape while a frame is in flight"
    local BLOG="$OUT/B-midflight-close.log"
    # The synthetic pipeline's ~9 s frames are the only "mid-flight" window
    # (the demo transform completes in ~0.04 s at the new geometry).
    launch "$BLOG" CENTRESTAGE_DLSS_SYNTHETIC=1
    check "B: the app received input (F12 self-screenshot)" grab_focus "$BLOG"
    local shots_before
    shots_before=$(shots_in "$BLOG")
    re_focus
    send_key 67                                    # F9 -> Processing
    sleep 1                                                 # comfortably mid-flight (~9 s)
    send_key 1                                     # ESC mid-flight (handled while evaluating)
    send_key 88                                    # mid-flight screenshot (the pump must keep running)
    check "B: the Esc key was delivered (key log)" \
        grep -q "key: Esc" "$BLOG"
    check "B: the app kept painting after the close request (F12 screenshot landed)" \
        wait_new_shot "$BLOG" "$shots_before" 20
    check "B: the in-flight frame still completed (teardown deferred, not torn down)" \
        wait_log "$BLOG" "dlss: frame 1 processed" 90
    check "B: the close request was handled mid-flight (deferral logged before completion)" \
        deferral_before_completion "$BLOG"
    check "B: clean exit once the frame landed (rc 0)" close_app "$BLOG"
}

deferral_before_completion() { # deferral_before_completion <log-file> — the
    # app logged the close deferral on a frame that ran before the completion
    # line: the request was handled while the worker was still in flight.
    local log="$1" defline doneline
    defline=$(grep -n "dlss: close deferred" "$log" | head -1 | cut -d: -f1)
    doneline=$(grep -n "frame 1 processed" "$log" | head -1 | cut -d: -f1)
    [ -n "$defline" ] && [ -n "$doneline" ] && [ "$defline" -lt "$doneline" ]
}

run_C() {
    say "scenario C: fault injection (CENTRESTAGE_DLSS_FAULT=throw-once) + retry"
    local CLOG="$OUT/C-fault-injection.log"
    launch "$CLOG" CENTRESTAGE_DLSS_SYNTHETIC=1 CENTRESTAGE_DLSS_FAULT=throw-once
    check "C: the app received input (F12 self-screenshot)" grab_focus "$CLOG"
    re_focus
    send_key 67                                    # F9 -> the injected fault
    check "C: the faulted worker was handled without crashing the app" \
        wait_log "$CLOG" "DLSS evaluate faulted" 30
    re_focus
    send_key 67                                    # retry from Failed
    check "C: the retry recovered and processed a frame" \
        wait_log "$CLOG" "dlss: frame 1 processed" 90
    check "C: the recovered frame was screenshot (F12 key path)" f12_shoot "$CLOG"
    check "C: the recovered capture has real content (non-blank)" shot_has_content "$CLOG"
    check "C: clean exit after Escape" close_app "$CLOG"
}

run_D() {
    say "scenario D: the F10 A/B toggle (the visible improvement path)"
    local DLOG="$OUT/D-ab-toggle.log"
    launch "$DLOG"
    check "D: the app received input (F12 self-screenshot)" grab_focus "$DLOG"
    re_focus
    send_key 67                                    # F9 -> one frame
    check "D: F9 reached the seam through the key wiring" \
        wait_log "$DLOG" "dlss: F9 requested one frame" 10
    check "D: the frame was processed (worker + presenter)" \
        wait_log "$DLOG" "dlss: frame 1 processed" 90
    re_focus
    send_key 68                                    # F10 -> the A/B swap
    check "D: F10 swapped the presented view to the plain stretch baseline" \
        wait_log "$DLOG" "F10 -> plain stretch baseline" 12
    check "D: the swapped view was screenshot (F12 key path)" f12_shoot "$DLOG"
    check "D: the swapped capture has real content (non-blank)" shot_has_content "$DLOG"
    re_focus
    send_key 68                                    # F10 -> back
    check "D: F10 swapped back to the classical sharp stretch" \
        wait_log "$DLOG" "F10 -> classical sharp stretch" 12
    check "D: clean exit after Escape" close_app "$DLOG"
}

case "$ONLY" in
    A) run_A ;;
    B) run_B ;;
    C) run_C ;;
    D) run_D ;;
    *) run_A; run_B; run_C; run_D ;;
esac

# --- summary ---------------------------------------------------------------
echo
echo "verify-seam-risks ($ONLY): $PASS passed, $FAIL failed"
echo "evidence: $OUT"
ls "$OUT" | sed 's/^/  /'
[ "$FAIL" -eq 0 ]
