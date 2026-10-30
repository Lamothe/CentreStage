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

Mouse: left-drag orbit, right/middle-drag pan, scroll zoom, double-click or F11 toggles
fullscreen, F9 processes one frame through the DLSS seam (see below), F12 saves a
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

## Desktop integration (GNOME)

`linux/install.sh` publishes the viewer to `~/.local/share/centrestage`, installs a
`.desktop` entry and icon, and makes Centre Stage the default handler for `.glb`/`.gltf`
(double-click in Files). Launching Centre Stage with no arguments opens a file picker.
Re-run the script after pulling changes; it is idempotent.

## DLSS seam (dlss5-linux native host — demonstration transform, NOT DLSS5)

An optional seam wires the tested C host ABI of the dlss5-linux project
(`vr/native-host.h`, built into `libdlss5_nr_host.so` by
`linux/build-native-host.sh`; `DLSS5_LINUX=/path/to/dlss5-linux` selects the
checkout): the current view is rendered into a low-resolution framebuffer (the
seam's capture geometry), read back, processed on a worker thread, and the returned
frame is presented fullscreen with the in-band label in the status text.

By default the seam runs the **demonstration transform** (NOT DLSS5): a
real openly documented Lanczos3 upscaler with real weights (dlss5-linux's
`vr/NATIVE-DEMO-UPSCALE.md`) behind the identical ABI/capture/present
path. Geometry (the seam's knob): a **half-window capture upscaled x2**
(640x400 -> 1280x800 at the default window) - the geometry that looks
sharp; the earlier 64x72 x4 capture is imperceptible (Michael's F9
verdict) and stays available as the "extreme" stress case
(`CENTRESTAGE_DLSS_EXTREME=1`; `CENTRESTAGE_DLSS_SYNTHETIC=1` selects
the synthetic pipeline at 64x72). Measured quality at the x2 ratio (the
harness's generated source vs a bilinear baseline): PSNR 20.49 vs
19.62 dB, SSIM 0.9138 vs 0.9067 - the SSIM margin is small and stated
plainly; the case is the visible A/B, not the number. **F10 swaps the
presented result between the Lanczos3 transform and the bilinear
baseline at the SAME capture geometry** (evidence:
`.evidence/dlss-demo-ab/{processed,baseline}.png` differ in 1.2% of RGB bytes). The label the seam displays with every presented frame is
exactly:

    not DLSS5 - demonstration transform with real weights

**Every presented frame carries that label. This proves the platform end
to end; it is NOT a DLSS5 quality claim and must never be conflated with
one.** Processing is frame-by-frame (F9-triggered) by design; the
demonstration transform itself costs milliseconds at this geometry.

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
arrives it pins fullscreen with the mode's label and the measured
latency. Any camera input (or F9 again) returns to the live view. In the
stateless demonstration mode the frames-since-reset counter stays 0; the
synthetic path sequences temporally across frames. Nothing changes when
the seam is idle.

`--dlss-demo outdir` drives the same path unattended: it screenshots the
live view (`live.png`), the in-flight frame (`processing.png`) and the
presented result (`processed.png` = Lanczos3 and `baseline.png` = bilinear, one frame
apart so the swap is actually presented), writes `report.txt`
(label, geometry, measured latency, counter, arena high-water) and closes.
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
the app and a retry from Failed recovers). Latest full run: 18/19 checks
pass — the one failure is the documented mouse gap below (evidence in
`.evidence/seam-risks-v3/`).

**Input-injection reality (documented, not overclaimed):** the unattended
input layer (ydotoold uinput) is STOCHASTIC on this session - events are
lost randomly under CPU load and after idle periods, so no single suite
run is complete. The behaviour matrix is proven across runs
(`linux/verify-seam-risks.sh`, scenarios A-D; runs v3-v7; run v7's
scenario A flaked entirely at the injection layer ("the app received
input" failed first) while B/C/D passed):

| path                                   | green evidence |
|----------------------------------------|----------------|
| F9 Trigger + worker/presenter          | v3, v5, v6     |
| mid-flight close/Esc gating (deferral) | v3, v5         |
| worker-fault handling + retry          | v3, v5, v6     |
| F10 A/B toggle (the visible demo)      | v4, v6, v7     |
| camera-drag Release (mouse wiring)     | v6             |
| clean exit (rc 0)                      | v3, v4, v5, v6 |
| F12 screenshot + non-blank content     | v3-v6          |

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
