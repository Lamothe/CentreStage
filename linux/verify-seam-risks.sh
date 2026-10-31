#!/bin/bash
# verify-seam-risks.sh — behavioural verification of the DLSS seam risk paths
# that code review could only build-verify:
#
#   A. interactive F9 Trigger wiring + the worker/presenter path + Esc close
#      (the camera-drag Release goes through the app's control channel: the
#      same camera handler and release path; OS pointer delivery is a manual
#      check in the README)
#   B. mid-flight close/Esc gating: the Esc is handled while a frame is in
#      flight (asserted on the app's "close deferred" line preceding the
#      completion line), the app keeps painting, completes the frame and
#      exits rc 0
#   C. worker-fault crash path + retry from Failed (fault injection via the
#      test-only CENTRESTAGE_DLSS_FAULT=throw-once hook)
#   D. the F10 A/B toggle (the visible improvement path)
#   E. mouse controls (synthetic clicks through the real handlers)
#
# Input policy: seat-level input injection (ydotool/uinput) is BANNED —
# injected events go to whatever window happens to have keyboard focus (it
# once typed into the user's browser). Two exact-target mechanisms are used
# instead:
#
#   * XSendEvent key events: linux/window-key.sh <pid> <key> <log> discovers and
#     validates the app's X11 window, then sends directly to that window;
#     it never uses XTEST, focus changes, or seat-level input;
#   * driving the app (screenshots, close, camera drag): the app's control
#     FIFO (CENTRESTAGE_CTL_FIFO, see src/CentreStage/ControlChannel.cs),
#     which addresses the exact app instance and calls the same handler
#     entry points the input events call.
#
# Every key that must land is asserted via its effect on the app log (the F9
# request line, the mid-flight deferral line, the screenshot line or the
# clean rc-0 exit); the F12 captures are additionally checked for real
# (non-blank) content. OS-level pointer delivery itself remains a documented
# manual check.
#
# Unattended: no dialogs, no interaction, no session-wide settings changes.
# An idle/blanked desktop may throttle rendering and make the suite time out.
# The app runs forced onto X11 (the
# linux/run-centrestage.sh recipe), where placement is deterministic: the
# window is parked on the secondary display via CENTRESTAGE_WINDOW_POS.
#
# Requirements: built CentreStage (dotnet build), a graphical session, and
# xdotool (system, or the bundled one in .tools/xdotoolroot). Evidence (logs +
# screenshots) is written to the output directory; NOTHING here enters Git.
#
# CENTRESTAGE_RISK_ONLY=A|B|C|D|E runs one scenario (each scenario starts its own
# app instance; a single invocation runs A through E).
set -u

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
# The demo subject (the earlier pixel-girl assets were deleted; the suite
# now uses Michael's chosen test asset, same as the evidence runs).
MODEL="${CENTRESTAGE_RISK_MODEL:-/home/michael/Projects/UniRig/work/Teen_Kitty/Teen_Kitty_rigged.glb}"
if [ "$#" -gt 1 ]; then echo "usage: $0 [new-evidence-dir]" >&2; exit 2; fi
ONLY="${CENTRESTAGE_RISK_ONLY:-all}"
case "$ONLY" in all|A|B|C|D|E) ;; *) echo "error: invalid scenario $ONLY" >&2; exit 2 ;; esac
if [ "$#" -eq 1 ]; then
    mkdir -- "$1" || { echo "error: evidence directory must be new" >&2; exit 2; }
    OUT="$(cd "$1" && pwd)"
else
    mkdir -p "$ROOT/.evidence"
    OUT="$(mktemp -d "$ROOT/.evidence/seam-risks-XXXXXXXX")" || exit 2
fi
PASS=0
FAIL=0

if [ ! -x "$ROOT/linux/window-key.sh" ]; then
    echo "error: linux/window-key.sh missing or not executable" >&2
    exit 2
fi
if ! python3 -c "import PIL" 2>/dev/null; then
    echo "error: python3-Pillow is required (the capture content check)" >&2
    exit 2
fi

# Launch the executable directly, so APP_PID is the owned window process.
APP="$ROOT/src/CentreStage/bin/Release/net10.0/CentreStage"
if [ ! -x "$APP" ]; then echo "error: build Release first" >&2; exit 2; fi
RUN_DIR="$(mktemp -d "$OUT/.run-XXXXXXXX")" || exit 2
cc -std=c11 -Wall -Wextra -Werror "$ROOT/linux/owned-process.c" -o "$RUN_DIR/owner" || exit 2
APP_PID=""
OWNER_PID=""
OWNER_FD=""
STATUS_FILE=""
WIN_PID=""
APP_LOG=""
CTL_FIFO=""
restore() {
    if [ -n "$OWNER_PID" ]; then
        printf 't' >&"$OWNER_FD" 2>/dev/null || :
        for _ in {1..20}; do [ -f "$STATUS_FILE" ] && break; sleep 0.1; done
        if [ ! -f "$STATUS_FILE" ]; then printf 'k' >&"$OWNER_FD" 2>/dev/null || :; fi
        exec {OWNER_FD}>&-
        wait "$OWNER_PID" 2>/dev/null || :
    fi
    rm -rf -- "$RUN_DIR"
}
trap restore EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

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
    colors=$(python3 -c 'from PIL import Image; import sys; im = Image.open(sys.argv[1]).convert("RGB"); print(len(set(im.getdata())))' "$shot")
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

send_key() { # send_key <key> — real key events to the EXACT app window
    # (linux/window-key.sh: direct XSendEvent). Never to the seat.
    "$ROOT/linux/window-key.sh" "$WIN_PID" "$1" "$APP_LOG"
}

ctl() { # ctl <command...> — drive the app through its control FIFO (the
    # exact app instance; no OS input). Timeout: a dead app must not hang
    # the suite on the FIFO open.
    timeout 10 bash -c 'printf "%s\n" "$1" > "$2"' _ "$*" "$CTL_FIFO" \
        || { echo "WARN: ctl '$*' not delivered (app gone?)"; return 1; }
}

launch() { # launch <log-file> [env VAR=...]
    local log="$1"; shift
    if [ -n "$OWNER_PID" ]; then
        echo "error: previous owned app was not reaped; aborting suite" >&2
        exit 2
    fi
    APP_LOG="$log"
    CTL_FIFO="$RUN_DIR/ctl-$(date +%s%N).fifo"
    if ! mkfifo -m 600 "$CTL_FIFO"; then
        echo "error: mkfifo $CTL_FIFO failed" >&2
        exit 2
    fi
    # The owner reserves the child PID until reaping and uses a pidfd for all
    # signals. EOF on its private command pipe also cleans up on runner death.
    local id_file="$RUN_DIR/pid-$(date +%s%N)"
    STATUS_FILE="$id_file.status"
    local command_fifo="$id_file.commands"
    mkfifo -m 600 "$command_fifo" || exit 2
    exec {OWNER_FD}<>"$command_fifo"
    ( cd "$ROOT" && exec {OWNER_FD}>&- && exec "$RUN_DIR/owner" "$log" "$id_file" "$STATUS_FILE" \
      env XDG_SESSION_TYPE=x11 WAYLAND_DISPLAY=wayland-none \
          CENTRESTAGE_WINDOW_POS=3480,120 CENTRESTAGE_LOG_KEYS=1 \
          CENTRESTAGE_CTL_FIFO="$CTL_FIFO" CENTRESTAGE_SHOT_DIR="$OUT" "$@" \
          CENTRESTAGE_AUTO_CLOSE_MS="${RISK_AUTO_CLOSE_MS:-600000}" \
          "$APP" "$MODEL" <"$command_fifo" ) &
    OWNER_PID=$!
    for _ in {1..100}; do [ -s "$id_file" ] && break; sleep 0.1; done
    if [ ! -s "$id_file" ]; then echo "error: owned process did not start" >&2; exit 2; fi
    APP_PID="$(cat "$id_file")"
    WIN_PID="$APP_PID"
    if ! wait_log "$log" "dlss: capture .* presented" 90; then
        echo "error: app did not initialise its seam; see $log" >&2
        exit 2
    fi
}

probe_input() { # probe_input <log-file> — prove key delivery to the exact
    # window: an F12 whose screenshot lands in the app log.
    local log="$1" before
    before=$(shots_in "$log")
    send_key F12 || return 1
    wait_new_shot "$log" "$before" 30
}

f12_shoot() {
    local log="$1" before
    before=$(shots_in "$log")
    send_key F12 || return 1
    wait_new_shot "$log" "$before" 40
}

finish_owned() {
    local limit="$1" i=0 rc
    while [ "$i" -lt "$limit" ] && [ ! -s "$STATUS_FILE" ]; do sleep 1; i=$((i + 1)); done
    if [ ! -s "$STATUS_FILE" ]; then echo "WARN: app did not exit" >&2; return 1; fi
    rc="$(cat "$STATUS_FILE")"
    printf 'w' >&"$OWNER_FD" || return 1
    exec {OWNER_FD}>&-
    wait "$OWNER_PID" 2>/dev/null || :
    OWNER_PID=""
    APP_PID=""
    WIN_PID=""
    if [ "$rc" != 0 ]; then echo "WARN: app exited with code $rc" >&2; return 1; fi
    return 0
}

close_app() { # close_app <log-file> — a clean exit means rc 0; the Escape is
    # sent to the exact window, so an exited app can never receive it.
    [ ! -f "$STATUS_FILE" ] || return 1
    send_key Escape || return 1
    finish_owned 30
}

close_auto() { # close_auto <log-file> — the auto-close hook exits the app;
    # no keys, no control-channel commands.
    finish_owned 60
}

run_A() {
    say "scenario A: F9 Trigger wiring + camera drag Release (window-exact keys; control-channel drag)"
    local ALOG="$OUT/A-interactive.log"
    launch "$ALOG"
    check "A: key delivery to the exact window (F12 self-screenshot)" probe_input "$ALOG"
    send_key F9 || { check "A: send F9" false; return; }
    check "A: F9 reached the seam through the key wiring" \
        wait_log "$ALOG" "dlss: F9 requested one frame" 10
    check "A: the frame was processed (worker + presenter)" \
        wait_log "$ALOG" "dlss: frame 1 processed" 90
    check "A: the presented frame was screenshot (F12 key path)" f12_shoot "$ALOG"
    check "A: the captured frame has real content (non-blank)" shot_has_content "$ALOG"
    sleep 3                                                # let the event pump catch up
    # The drag goes through the app's control channel: same camera handler
    # and release path as a mouse drag (OS pointer delivery is the README's
    # manual check).
    ctl orbit -0.2 0.1 || { check "A: send orbit" false; return; }
    check "A: the camera drag released the presented result (camera handler)" \
        wait_log "$ALOG" "dlss: camera input released the presented result" 12
    check "A: clean exit after Escape" close_app "$ALOG"
}

run_B() {
    say "scenario B: Escape while a frame is in flight"
    local BLOG="$OUT/B-midflight-close.log"
    # The synthetic pipeline's ~9 s frames are the only "mid-flight" window
    # (the demo transform completes in ~0.04 s at the new geometry).
    launch "$BLOG" CENTRESTAGE_DLSS_SYNTHETIC=1
    check "B: key delivery to the exact window (F12 self-screenshot)" probe_input "$BLOG"
    local shots_before
    shots_before=$(shots_in "$BLOG")
    send_key F9 || { check "B: send F9" false; return; }
    wait_log "$BLOG" "F9 requested one frame" 30 || { check "B: F9 received" false; return; }
    send_key Escape || { check "B: send Escape" false; return; }
    wait_log "$BLOG" "dlss: close deferred" 30 || { check "B: deferral received" false; return; }
    send_key F12 || { check "B: send F12" false; return; }
    check "B: the Esc key was delivered (key log)" \
        wait_log "$BLOG" "key: Escape" 30
    check "B: the app kept painting after the close request (F12 screenshot landed)" \
        wait_new_shot "$BLOG" "$shots_before" 20
    check "B: the in-flight frame still completed (teardown deferred, not torn down)" \
        wait_log "$BLOG" "dlss: frame 1 processed" 90
    check "B: the close request was handled mid-flight (deferral logged before completion)" \
        deferral_before_completion "$BLOG"
    check "B: deferred close exits once the frame lands (rc 0)" close_auto "$BLOG"
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
    check "C: key delivery to the exact window (F12 self-screenshot)" probe_input "$CLOG"
    send_key F9 || { check "F9 send" false; return; }   # -> the injected fault
    check "C: the faulted worker was handled without crashing the app" \
        wait_log "$CLOG" "DLSS evaluate faulted" 30
    send_key F9 || { check "F9 send" false; return; }   # retry from Failed
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
    check "D: key delivery to the exact window (F12 self-screenshot)" probe_input "$DLOG"
    send_key F9 || { check "F9 send" false; return; }   # -> one frame
    check "D: F9 reached the seam through the key wiring" \
        wait_log "$DLOG" "dlss: F9 requested one frame" 10
    check "D: the frame was processed (worker + presenter)" \
        wait_log "$DLOG" "dlss: frame 1 processed" 90
    send_key F10 || { check "F10 send" false; return; } # -> the A/B swap
    check "D: F10 swapped the presented view to the plain stretch baseline" \
        wait_log "$DLOG" "dlss: F10 -> plain stretch baseline" 12
    check "D: the swapped view was screenshot (F12 key path)" f12_shoot "$DLOG"
    check "D: the swapped capture has real content (non-blank)" shot_has_content "$DLOG"
    check "D: exactly one F10 event selected the baseline" \
        test "$(grep -c '^dlss: F10 ->' "$DLOG")" -eq 1
    send_key F10 || { check "F10 send" false; return; } # -> back
    check "D: F10 swapped back to the classical sharp stretch" \
        wait_log "$DLOG" "dlss: F10 -> classical sharp stretch" 12
    sleep 2 # detect late duplicates before declaring the final count
    check "D: exactly two F10 events selected two views" \
        test "$(grep -c '^dlss: F10 ->' "$DLOG")" -eq 2
    check "D: clean exit after Escape" close_app "$DLOG"
}

run_E() {
    local ELOG="$OUT/E-button.log" MLOG="$OUT/E-menu.log"
    say "scenario E: mouse controls (synthetic clicks through the real handlers)"
    # OS-level pointer delivery is a MANUAL check (README): the synth hooks
    # drive the SAME handler entry points real mouse events call
    # (HandleMouseDown / SynthMenuPick); Silk's own MouseDown dispatch is the
    # only untested hop.
    # Button centre at the 1280x800 default window: (1280-14-160, 14+19).
    # Auto-close instead of Escape (see close_auto); scenario-local
    # so no later scenario inherits the short timeout.
    local RISK_AUTO_CLOSE_MS=60000
    launch "$ELOG" CENTRESTAGE_SYNTH_CLICK="1106,33"
    check "E: the button click reached the demo control" \
        wait_log "$ELOG" "synth: click consumed by the demo control" 45
    check "E: the button click requested one frame (F9 path)" \
        wait_log "$ELOG" "F9 requested one frame" 30
    check "E: the frame was processed (worker + presenter)" \
        wait_log "$ELOG" "frame 1 processed" 60
    check "E: clean auto-exit (rc 0)" close_auto "$ELOG"

    launch "$MLOG" CENTRESTAGE_SYNTH_MENU="600,400,0"
    check "E: the context menu opened at the click point" \
        wait_log "$MLOG" "synth: menu at (600,400) item 0" 45
    check "E: the menu pick requested one frame (F9 path)" \
        wait_log "$MLOG" "F9 requested one frame" 30
    check "E: the menu-picked frame was processed" \
        wait_log "$MLOG" "frame 1 processed" 60
    check "E: clean auto-exit (rc 0)" close_auto "$MLOG"
}

case "$ONLY" in
    A) run_A ;;
    B) run_B ;;
    C) run_C ;;
    D) run_D ;;
    E) run_E ;;
    *) run_A; run_B; run_C; run_D; run_E ;;
esac

# --- summary ---------------------------------------------------------------
echo
echo "verify-seam-risks ($ONLY): $PASS passed, $FAIL failed"
echo "evidence: $OUT"
ls "$OUT" | sed 's/^/  /'
[ "$FAIL" -eq 0 ]
