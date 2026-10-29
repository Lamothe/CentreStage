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
fullscreen, F9 processes one frame through the DLSS seam (see below), Esc quits. The
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

## DLSS seam (dlss5-linux native pipeline — synthetic weights)

An optional seam wires the tested C host ABI of the dlss5-linux project
(`vr/native-host.h`, built into `libdlss5_nr_host.so` by
`linux/build-native-host.sh`; `DLSS5_LINUX=/path/to/dlss5-linux` selects the
checkout): the current view is rendered into a 64x72 low-resolution
framebuffer, read back, evaluated by the native CPU pipeline on a worker
thread, and the returned frame is presented fullscreen (nearest upscale)
with the in-band label in the status text.

**This is on-demand frame-by-frame processing with visible latency — not
real-time upscaling.** The evaluator costs seconds per frame (measured
**8.7 s** worker wall-clock at the 64x72 demo geometry on an idle RTX 4070
Ti SUPER host; ~10.8 s capture-to-presented). The boundary refuses frames
whose padded extent exceeds its 320x320 working floor. Note: on a session
that has gone idle-blanked (GNOME's idle-delay), the compositor throttles
the window's render cadence — the screenshots and the evaluator still work,
but the app itself paints rarely until the session wakes.
The weights are SYNTHETIC: `dlss5_nr_label()` returns "synthetic weights —
arbitrary transform" and the seam displays it with every presented frame.
The transform really is arbitrary — with the seeded synthetic weights it
rails some channels (e.g. G->0, B->255) while others track input structure.

Workflow: **F9** captures the current view and starts one evaluation; the
live view keeps rendering while "processing... seconds per frame, not
real-time" shows; when the result arrives it pins fullscreen with the
label, the measured latency and the frames-since-reset counter. Any camera
input (or F9 again) returns to the live view; the temporal sequence
continues across frames. Nothing changes when the seam is idle.

`--dlss-demo outdir` drives the same path unattended: it screenshots the
live view (`live.png`), processes one frame, screenshots the presented
result (`processed.png`, label in-band), writes `report.txt` (label,
geometry, measured latency, counter, arena high-water) and closes. Env:
`CENTRESTAGE_DLSS_LIB` (explicit library path), `CENTRESTAGE_WINDOW_POS`
("x,y" window placement — honoured on X11/XWayland; the unattended runs
park the window on the secondary display).

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
