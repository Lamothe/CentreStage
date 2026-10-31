# Scene pair capture

Linux, OpenGL 3.3, .NET 10 and a working display server are required. Supply the
read-only Teen Kitty GLB snapshot with SHA-256
`ab34f516d6bb8d337e7607c337d1158726b46e15b2880391f7d48bb76b83f22a`.
Only this self-contained snapshot is accepted; do not use other people's assets
or NVIDIA assets. The output parent must exist; the final output directory must
not exist, even as a dangling symlink. Keep the parent and new output directory
under your exclusive control during capture. A failed or interrupted run can
leave partial data; **only a completed run with `metadata.json` is usable**.
Never resume or overwrite a failed run; choose a new directory. Free space is
required for 24 images, two renderers and the decoded asset.

```sh
dotnet run --project src/CentreStage -- \
  /home/michael/Projects/dlss5-linux/.scratch/scene-assets/Teen_Kitty_rigged-ab34f516d6bb8d337e7607c337d1158726b46e15b2880391f7d48bb76b83f22a.glb \
  --scene-pairs /path/to/new-output-directory
```

Capture rejects non-empty diagnostic environment variables: `CENTRESTAGE_SOLID`,
`CENTRESTAGE_WIREFRAME`, `CENTRESTAGE_STATUS_TEST`, `CENTRESTAGE_AUTO_CLOSE_MS`,
`CENTRESTAGE_SYNTH_CLICK`, `CENTRESTAGE_SYNTH_MENU`,
`CENTRESTAGE_SYNTH_MENU_SHOT`, `CENTRESTAGE_CTL_FIFO`,
`CENTRESTAGE_DLSS_SYNTHETIC`, `CENTRESTAGE_DLSS_EXTREME`, and
`CENTRESTAGE_DLSS_FAULT`. It creates an invisible window, does not create an
input context or inject input, and does not invoke the DLSS seam. It refuses
an unexpected framebuffer size rather than silently scaling.

Predeclared groups: train (8 views), validation (2), locked test (2). Each
split has its own yaw interval, pitch and directional light/ambient combination;
all use bind pose, no animation, same asset and per-view framing. Asset lights
are replaced by the explicitly specified single directional light. Train, val and
test TSV files contain relative `cheap.ppm<TAB>full.ppm` paths with no header,
compatible with `scene-train`. The locked test set must not be used for model
selection. Frames are same-resolution 256×256 P6 RGB8 in top-down row order,
without overlays.

The two per-frame renderers are the full PBR pass and a **cheap pass with
genuinely reduced texture and lighting fidelity** — not a resolution reduction
and not a fabricated perturbation of the full render:

- **Texture fidelity:** the base-colour map is sampled with a fixed +2.0 mip LOD
  bias (a quarter-size linear footprint per fetch, cheaper sampling on the real
  mip chain); all auxiliary material maps (metallic-roughness, normal, occlusion,
  emissive, specular, clearcoat, sheen, transmission) are not sampled at all and
  only their factors apply.
- **Lighting fidelity:** Lambert diffuse plus hemispheric ambient and
  factor-only emissive. The GGX specular lobe, clearcoat, sheen, transmission
  and normal-map perturbation are omitted.

Alpha handling, the unlit path, tone mapping and the sRGB encode match the full
pass, so both renders share one colour pipeline. Every capture verifies the
cheap and full rasters actually differ: a bit-identical pair aborts the run
(it would be an identity control, not training data). Each frame also records
`cheapRenderMs`/`fullRenderMs`, wall-clock milliseconds around draw plus
`gl.Finish` at this resolution on this driver — honest measurements of that run,
noisy at this scale, not real-time evidence and not a general performance claim.

`metadata.json` records the asset and image SHA-256 hashes,
absolute camera and lighting parameters, format, pose and GL implementation,
the cheap-pass description, and per frame the render timings and the
cheap-versus-full raster difference (`cheapFullMaxAbsDiff`,
`cheapFullMeanAbsDiff`). The schema is `centrestage-scene-pairs-v2`.
The final metadata file is the completion marker. Archive the source/build,
command and logs as well. Frame specification is deterministic, but OpenGL
rasterisation and bytes can differ between drivers; hashes record the actual
run, not a promise of cross-driver bit identity. Disjoint views and lighting do
not imply independence or generalisation to other scenes, poses or assets;
inspect for duplicates and correlation. These captures are training data for a
**neural rendering demonstration, not DLSS5**, and make no quality claim.
