# Centre Stage

A GLB/glTF 2.x viewer in C# — written to understand the format end to end rather than
wrap someone else's loader. Exhibit (the GNOME viewer) is a Python/GTK settings shell over
libf3d/VTK; when VTK's glTF importer refuses a file or ignores an extension, no amount of
Python can intervene. Here the whole pipeline is in reach.

![Centre Stage viewing a glTF model](docs/CentreStage1.png)
![Centre Stage viewing a glTF model](docs/CentreStage2.png)

## Layout

```
src/CentreStage.Core/     parsing and scene assembly (no rendering dependencies)
  Glb/GlbFile.cs      GLB container: header, JSON chunk, BIN chunk
  Schema/             typed DTOs for the glTF manifest
  BufferData.cs       buffer -> bufferView -> accessor decode (strides, sparse, normalised)
  GltfModel.cs        loads a file into runtime meshes/nodes/materials/textures/lights/skins
src/CentreStage/   Silk.NET OpenGL 3.3 renderer
  ShaderSources.cs    metallic-roughness PBR + extension shading
  Renderer.cs         geometry upload, CPU skinning, per-material uniforms
  OrbitCamera.cs      orbit/pan/zoom with auto-framing
tests/CentreStage.Core.Tests/  xUnit tests over hand-built in-memory GLBs
```

## Usage

```
dotnet run --project src/CentreStage -- <model.glb>              # interactive viewer
dotnet run --project src/CentreStage -- --inspect <model.glb>    # dump structure, no window
dotnet run --project src/CentreStage -- <model.glb> --capture out.png   # offscreen render
dotnet run --project src/CentreStage -- <model.glb> --dlss-demo outdir  # unattended DLSS evidence run
```

Mouse: left-drag orbit, middle-drag pan, scroll zoom, double-click or F11 toggles
fullscreen (right-click opens the demo's context menu while the seam is
active, so it no longer pans), F9 processes one frame through the DLSS
seam (see below), F12 saves a
screenshot of the frame just drawn (`CENTRESTAGE_SHOT_DIR`, default `./shots`), Esc quits. The
interactive viewer opens immediately and shows progress ("Parsing",
"Uploading textures n/N", "Closing") via an antialiased TrueType status overlay
(rasterised from a system sans face; a small bitmap font is the last-resort fallback):
parsing and texture decoding run on a background thread, and textures upload
incrementally per frame. Exit codes: 0 on clean close, 2 if the model failed to load.

Diagnostic env vars: `CENTRESTAGE_WIREFRAME=1` (no culling/depth), `CENTRESTAGE_SOLID=1|2`
(solid-colour shaders; mode 2 ignores vertex attributes entirely), `CENTRESTAGE_AUTO_CLOSE_MS=N`
(self-close after N ms — exercises the full open/close lifecycle unattended),
`CENTRESTAGE_STATUS_TEST=1` (with `--capture`: draws the status overlay after the model to
verify overlay text survives the renderer's GL state).

## Desktop integration (GNOME) — open from Files

`linux/install.sh` publishes the viewer to `~/.local/share/centrestage`, installs a
`.desktop` entry and icon, and makes Centre Stage the default handler for `.glb`/`.gltf`
(**double-click a .glb/.gltf in the Files application opens it here**).
Launching Centre Stage with no arguments opens a file picker.
Re-run the script after pulling changes; it is idempotent — and required after
every change, or Files launches stale code. It also builds the native seam
library and ships it with the publish, and copies the demonstration models
into `~/.local/share/dlss5-demo` (the app's first model-search path) when
they exist in the dlss5-linux checkout — without them the install warns and
the app runs with the classical demo only.

The desktop entry launches forced onto X11/XWayland
(`XDG_SESSION_TYPE=x11 WAYLAND_DISPLAY=wayland-none`, the same recipe as
`linux/run-centrestage.sh`): under native Wayland the compositor owns
window placement and the window lands wherever it decides (on this
two-display setup, usually the wrong screen). Verified unattended
(30/10/2026, evidence in `.evidence/files-launch/`): a `gio open` launch
(what Files runs) opens the published app as an X11 client, the window
maps at (50,50) on the Dell primary display (`xdotool getwindowgeometry`
output in `launch-geometry.txt`), it renders the model
(`shot-...png`, an F12 framebuffer capture), and the same published build
processes an F9 demo frame end to end (`published-demo.log`: model loaded,
12.19 s, label recorded).

## DLSS seam (dlss5-linux native host — demonstration transform, NOT DLSS5)

An optional seam wires the tested C host ABI of the dlss5-linux project
(`vr/native-host.h`, built into `libdlss5_nr_host.so` by
`linux/build-native-host.sh`; `DLSS5_LINUX=/path/to/dlss5-linux` selects the
checkout): the current view is rendered into a low-resolution framebuffer (the
seam's capture geometry), read back, processed on a worker thread, and the returned
frame is presented fullscreen with the view name in the status text (the
transform label is recorded in the demo report and console - see the
label policy below).

By default the seam runs the **neural demonstration transform** (NOT
DLSS5): a real learned upscaler with real openly licensed weights
(EDSR-base x2, Apache-2.0; dlss5-linux's `vr/NATIVE-DEMO-UPSCALE.md`) behind
the identical ABI/capture/present path. Geometry (the seam's knob): a
**half-window capture upscaled x2** (640x400 -> 1280x800 at the default
window); the earlier 64x72 x4 capture is imperceptible (Michael's F9
verdict) and stays available as the "extreme" stress case
(`CENTRESTAGE_DLSS_EXTREME=1`; `CENTRESTAGE_DLSS_SYNTHETIC=1` selects
the synthetic pipeline at 64x72). **F10 cycles the presented result
through the A/B views at the SAME capture** (one capture, re-processed):
neural -> plain stretch baseline (bilinear) -> classical sharp stretch
(Lanczos3). The transform label names the actual path and is **recorded with every
presented frame** (demo report + console): "not DLSS5 - demonstration
transform with real weights (EDSR-base x2, Apache-2.0)" on the neural
path, "not DLSS5 - classical demonstration scaler (...)" on the two
classical views, "synthetic weights - arbitrary transform" in synthetic
mode. The phrase "real weights" belongs only to the
neural path. This proves the platform end to end; it is NOT a DLSS5
quality claim and must never be conflated with one.** Processing is
frame-by-frame (F9-triggered) by design; the neural evaluate is ~11 s at
this geometry (the classical views are milliseconds).

**Mouse controls** (the demo runs from the mouse alone): an
**ENLARGE THIS VIEW (F9)** button sits at the top-right of the window -
state-aware, so it becomes **NEXT COMPARISON VIEW (F10)** and **BACK TO
LIVE VIEW** while a result is pinned, **WORKING...** while a frame is in
flight and **TRY AGAIN (F9)** after a failure. **Right-click anywhere**
opens the same controls as a context menu. Screen policy: the app carries
**no "not DLSS5" banner** (Michael's UI preference) - the presented state
names the view and what happened to the pixels; the transform label
("not DLSS5 - demonstration transform with real weights (EDSR...)") is
**recorded in the demo report and console**, never bannered on screen.

**The HUD explains itself in plain words** (a first-time viewer needs no
docs): the live view says what F9 does; the in-flight state reads "view
captured at half resolution - enlarging it now"; the presented state
names the view ("neural upscaler result" / "plain stretch baseline" /
"classical sharp stretch") and says what happened to the pixels.

**Test asset and framing** (this section's evidence): the subject is
`Teen_Kitty_rigged.glb` (Michael's choice after the pixel-girl assets
proved unreadable); the demo runs frame a documented **detail framing -
the top 35% of the model's height** (for a character: face, hair,
shoulders - the region where upscaling differences are visible; the
whole-body framing wastes the frame on dark background and an earlier
whole-car trial measured only a 2.1% A/B difference). The app's normal
view still frames the whole model (framing policy unchanged; the fit
itself changed - see below).

Camera fit note: the viewer now frames models with a projected-extent
fit (the model fills the view at the default pose; the old bbox-diagonal
fit left long models small). Orbiting far from the default pose can clip
extremes - zoom out. Near/far planes track the zoom distance.

Measured on that evidence (`.evidence/dlss-demo-edsr/report.txt`): the
neural result keeps **66%** of the live render's fine detail where the
classical sharp stretch keeps 14% and the plain stretch 6% (detail
energy = the Laplacian of the luminance in the pre-declared centre crop,
normalised to the live render). The accuracy scores favour the classical
views (SSIM 0.9792 sharp vs 0.9658 neural): blurring scores well against
a smooth reference - both numbers are reported rather than the flattering
one, and the zoom strip in `composite.png` shows what they mean. (Runs
drift by a few points with the slowly orbiting camera; each run's
`report.txt` is its own record.)

The synthetic network path remains available (the mode-off fallback,
`dlss5_nr_set_demo_transform(handle, 0)`): same-extent frames, temporally
sequenced, with `dlss5_nr_label()` = "synthetic weights — arbitrary
transform" — the synthetic weights really do make that transform
arbitrary (it rails some channels). It costs seconds per frame (measured
**8.7 s** worker wall-clock at the 64x72 demo geometry on an idle RTX
4070 Ti SUPER; ~10.8 s capture-to-presented). The synthetic path refuses frames
whose padded extent exceeds its 320x320 floor (demo-only handles serve
the demonstration transform up to 2048x2048). Note: on a session
that has gone idle-blanked (GNOME's idle-delay), the compositor throttles
the window's render cadence — the screenshots and the evaluator still
work, but the app itself paints rarely until the session wakes.

Workflow: **F9** captures the current view and starts one processing run;
the live view keeps rendering while "processing" shows; when the result
arrives it pins fullscreen with the view name and the measured
latency. Any camera input (or F9 again) returns to the live view. In the
stateless demonstration mode the frames-since-reset counter stays 0; the
synthetic path sequences temporally across frames. Nothing changes when
the seam is idle.

`--dlss-demo outdir` drives the same path unattended: it screenshots the
live view (`live.png`), the in-flight frame (`processing.png`) and one
file per view (`processed.png` = neural, `baseline.png` = plain stretch,
`lanczos3.png` = classical sharp), assembles `composite.png` - the four
self-labelled views in a 2x2 grid plus a strip of **2x-zoomed crops of
the centre quarter of the frame** (a fixed, pre-declared region, nested
inside the larger central-50% crop the quality numbers measure; this
strip is where the difference is visible to a human eye: the zoomed
neural-vs-plain pixel difference is ~8% of bytes vs ~2% full-frame) - writes a
plain-language `report.txt` (subject, what the numbers mean, the
detail-kept/sharpness/accuracy table with a computed verdict) and closes.
Env: `CENTRESTAGE_DLSS_LIB` (explicit library path), `CENTRESTAGE_WINDOW_POS`
("x,y" window placement — honoured on X11/XWayland; the unattended runs
park the window on the secondary display).

### Seam-risk verification and test hooks

`bash linux/verify-seam-risks.sh [evidence-dir]` verifies the seam's risky
paths behaviourally with real injected input (ydotool/uinput; `CENTRESTAGE_RISK_ONLY=A|B|C`
runs one scenario): **A** — F9 Trigger wiring, the worker+presenter path and
Esc close; **B** — the mid-flight close gating: the Esc is sent while a frame
is evaluating and the app's own "close deferred" line must precede the
"frame 1 processed" line (proving the request was handled mid-flight and the
teardown was deferred), the app keeps painting, completes the frame and
exits 0 (the exit code is checked);
**C** — the worker-fault path (`CENTRESTAGE_DLSS_FAULT=throw-once` makes the
first evaluation fault on the worker; the fault is handled without crashing
the app and a retry from Failed recovers). **E** — the mouse controls:
synthetic clicks (`CENTRESTAGE_SYNTH_CLICK="x,y"`,
`CENTRESTAGE_SYNTH_MENU="x,y,item"`) drive the same handler entry points
real mouse events call (the button click and the context-menu pick each
request a frame and process it; auto-close instead of injected Esc).
Scenario E run: **8/8** (`.evidence/seam-risks-E3/`). Honest scope: the
OS pointer delivery is a MANUAL check - both injection layers are broken
in this session (uinput mouse events never reach the window; xdotool
XTEST mis-maps coordinates onto the compositor surface - probed with an
extracted xdotool 3.20211022), so Silk's own MouseDown dispatch is the
only untested hop. Manual recipe: run the viewer, click **ENLARGE THIS
VIEW** (top-right) - a frame must process and pin; right-click must open
the menu; **NEXT COMPARISON VIEW** must swap the presented view.

Latest full run (v7d, the comprehensibility binary): **26/27** - A 6/7
(the mouse gap below only), B 6/6, C 6/6, D 8/8 (evidence:
`.evidence/seam-risks-v7d/` and `.evidence/seam-risks-v7d-D/`; the suite
defaults to the demo subject `Teen_Kitty_rigged.glb`,
`CENTRESTAGE_RISK_MODEL` overrides).

**Input-injection reality (documented, not overclaimed):** the unattended
input layer (ydotoold uinput) is STOCHASTIC on this session - events are
lost randomly under CPU load and after idle periods, so no single suite
run is complete. The behaviour matrix is proven across runs
(`linux/verify-seam-risks.sh`, scenarios A-D; runs v3-v7; run v7's
scenario A flaked entirely at the injection layer ("the app received
input" failed first) while B/C/D passed):

| path                                   | green evidence |
|----------------------------------------|----------------|
| F9 Trigger + worker/presenter          | v3, v5, v6, v7d |
| mid-flight close/Esc gating (deferral) | v3, v5, v7d    |
| worker-fault handling + retry          | v3, v5, v6, v7d |
| F10 A/B toggle (the visible demo)      | v4, v6, v7, v7d |
| camera-drag Release (mouse wiring)     | v6             |
| clean exit (rc 0)                      | v3-v7d         |
| F12 screenshot + non-blank content     | v3-v7d         |

The camera-drag path (previously a documented manual gap) PASSED with
real injected input in v6 - the earlier failures were injection flake,
not a wiring gap. **Manual fallback recipe** (if the injection flakes):
run the viewer, press F9, wait for the presented frame to pin, click-drag
in the window - the status must return to the live view and the console
must print `dlss: camera input released the presented result`.

Test/diagnostic hooks: the F12 screenshot key is always available (harmless
in normal use). Env-gated (all off by default): `CENTRESTAGE_DLSS_FAULT=throw-once`
(fault injection for the worker path), `CENTRESTAGE_LOG_KEYS=1` (log key
events), `CENTRESTAGE_SHOT_DIR` (screenshot directory),
`CENTRESTAGE_SYNTH_CLICK="x,y"` / `CENTRESTAGE_SYNTH_MENU="x,y,item"` /
`CENTRESTAGE_SYNTH_MENU_SHOT="x,y"` (scenario E's scripted clicks and the
menu-open evidence capture; never fired in `--dlss-demo` mode),
`CENTRESTAGE_AUTO_CLOSE_MS` (automatic close for unattended runs). The
window-close button cannot be deferred (Silk.NET's Closing event is not
cancellable): pressed while a frame is evaluating, teardown blocks for at
most the remaining evaluation time (~9 s, bounded; the worker is CPU-only
and its fault is observed) — Esc/auto-close defer instead and exit 0. Tooling notes for the automation:
ydotoold 1.0.4 silently dries up after the first key event (the runner gives
every key a fresh daemon), fast press+release pairs are dropped (the runner
uses `--key-delay`), xdotool cannot inject into Wayland windows, and the
session's launch-focus grant is unreliable (the runner clicks the window
before keys that must land).

## Extension support

The corpus this was built for uses extensions VTK's importer either refuses or silently
drops. Handled here:

- `KHR_materials_pbrSpecularGlossiness` (legacy model, shaded natively — the file Exhibit
  refuses outright because it is in `extensionsRequired`)
- `KHR_materials_specular`, `KHR_materials_ior`
- `KHR_materials_transmission`, `KHR_materials_clearcoat`, `KHR_materials_sheen`
- `KHR_materials_emissive_strength`, `KHR_materials_unlit`
- `KHR_texture_transform` (per-texture-slot, not "one transform for all" as VTK does)
- `KHR_lights_punctual`

Required extensions that carry *structural* data (Draco, meshopt, KTX2, quantisation) are
refused with the extension named, since the data cannot be recovered without a decoder.
Required-but-cosmetic extensions load anyway and are reported — the file is never
locked out for decoration.

## What works / what doesn't

Verified against a 34-file real-world corpus (all render correctly, including the
Exhibit-failing `pbrSpecularGlossiness` and `texture_transform`-required files), plus
21 unit tests over the parser maths.

Not yet: animation playback (structure is parsed), morph targets, KHR_materials_variants,
refraction-through-transmission (approximated against the environment colour), image-based
lighting (hemispheric ambient + key light stands in for a prefiltered probe). Blended
(`alphaMode: BLEND`) materials render in a second pass with standard alpha blending;
per-pixel sorting within a mesh is not done.
