using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using CentreStage.Core;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using StbImageSharp;

namespace CentreStage;

internal static class Program
{
    private static void Main(string[] args)
    {
        if (args.Length == 0)
        {
            // Desktop launchers pass no arguments: offer a file picker so the app is
            // useful from the GNOME application grid, then fall back to usage text.
            var picked = TryPickModelFile();
            if (picked is null)
            {
                PrintUsage();
                return;
            }

            args = [picked];
        }

        var inspect = args.Contains("--inspect");
        var capture = GetOption(args, "--capture");
        var frames = int.TryParse(GetOption(args, "--frames"), out var f) ? Math.Max(f, 1) : 3;
        var anim = int.TryParse(GetOption(args, "--anim"), out var a) ? a : 0;
        var time = float.TryParse(GetOption(args, "--time"), out var t) ? t : 0f;
        var noAnim = args.Contains("--no-anim");
        var path = args.FirstOrDefault(x => !x.StartsWith("--", StringComparison.Ordinal)
            && !IsOptionValue(args, x, "--capture", "--frames", "--anim", "--time"));
        if (path is null)
        {
            Console.Error.WriteLine("error: no model path given.");
            Environment.ExitCode = 1;
            return;
        }

        // Inspect and capture are CLI tools: load synchronously. The interactive viewer
        // loads on a background thread so the window opens immediately with status.
        if (inspect || capture is not null)
        {
            GltfModel model;
            try
            {
                model = GltfModel.Load(path);
            }
            catch (Exception ex) when (ex is UnsupportedExtensionsException or InvalidDataException or IOException
                or System.Text.Json.JsonException or ArgumentException or IndexOutOfRangeException
                or OverflowException or KeyNotFoundException)
            {
                Console.Error.WriteLine($"error: failed to load '{path}': {ex.Message}");
                Environment.ExitCode = 2;
                return;
            }

            PrintSummary(path, model);

            if (inspect)
            {
                Inspect(path, model);
                return;
            }

            RunCapture(path, model, capture!, frames, anim, time, !noAnim);
            return;
        }

        RunViewer(path, !noAnim);
    }

    private static void Inspect(string path, GltfModel model)
    {
        {
            if (model.Animations.Count > 0)
            {
                Console.WriteLine($"  animations: {model.Animations.Count}");
                for (var ai = 0; ai < model.Animations.Count; ai++)
                {
                    var anim = model.Animations[ai];
                    var paths = string.Join(", ", anim.Channels
                        .GroupBy(c => c.Path)
                        .Select(g => $"{g.Key} x{g.Count()}"));
                    var modes = string.Join(", ", anim.Samplers
                        .Select(s => s.Interpolation)
                        .Distinct());
                    Console.WriteLine(
                        $"    [{ai}] '{anim.Name ?? "(unnamed)"}' duration={anim.Duration:F2}s channels={anim.Channels.Count} ({paths}) interpolation={modes}");
                }
            }
            else
            {
                Console.WriteLine("  animations: none");
            }

            if (model.Skins.Count > 0)
            {
                Console.WriteLine($"  skins: {model.Skins.Count}");
                for (var si = 0; si < model.Skins.Count; si++)
                {
                    Console.WriteLine($"    [{si}] '{model.Skins[si].Name ?? "(unnamed)"}' joints={model.Skins[si].JointNodes.Count}");
                }
            }

            Console.WriteLine($"  nodes walked: {model.Nodes.Count} of {model.Root.Nodes?.Count ?? 0}");
            var meshNodes = model.Nodes.Where(n => n.MeshIndex >= 0).ToList();
            Console.WriteLine($"  nodes carrying meshes: {meshNodes.Count}");
            foreach (var n in meshNodes.Take(40))
            {
                Console.WriteLine($"    node '{n.Name}' mesh={n.MeshIndex} world t=({n.World.Translation:F3}) sx=({n.World.M11:F4},{n.World.M22:F4},{n.World.M33:F4})");
            }

            for (var mi = 0; mi < model.Meshes.Count; mi++)
            {
                var mesh = model.Meshes[mi];
                for (var pi = 0; pi < mesh.Primitives.Count; pi++)
                {
                    var prim = mesh.Primitives[pi];
                    var min = new Vector3(float.MaxValue);
                    var max = new Vector3(float.MinValue);
                    foreach (var p in prim.Positions)
                    {
                        min = Vector3.Min(min, p);
                        max = Vector3.Max(max, p);
                    }

                    var idxMax = prim.Indices.Max();
                    var idxFirst = string.Join(",", prim.Indices.Take(9));
                    var jmax = prim.Joints is { } jn
                        ? jn.Max(j => MathF.Max(MathF.Max(j.X, j.Y), MathF.Max(j.Z, j.W)))
                        : -1f;
                    var mat = prim.MaterialIndex >= 0 ? model.Materials[prim.MaterialIndex] : null;
                    Console.WriteLine($"  mesh[{mi}] prim[{pi}] verts={prim.Positions.Length} idx={prim.Indices.Length} mode={prim.Mode} alpha={mat?.AlphaMode ?? AlphaMode.Opaque} jmax={jmax} idxMax={idxMax} first=[{idxFirst}] raw bounds ({min:F3}) .. ({max:F3})");
                }
            }
        }
    }

    private static string? GetOption(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("usage: CentreStage <model.glb|model.gltf> [--inspect]");
        Console.WriteLine("       CentreStage --inspect <model.glb>");
        Console.WriteLine("       CentreStage <model.glb> --capture out.png [--frames N]");
    }

    /// <summary>
    /// File picker for the no-argument desktop launcher case, via zenity's
    /// GtkFileChooser dialog. Returns null (and lets the caller print usage) when
    /// zenity is missing, the user cancels, or no display is available.
    /// </summary>
    private static string? TryPickModelFile()
    {
        if (Environment.GetEnvironmentVariable("DISPLAY") is null
            && Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is null)
        {
            return null;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "zenity",
                UseShellExecute = false,
                // Stderr inherits the terminal. Redirecting it without draining would
                // deadlock if zenity ever wrote more than a pipe buffer of warnings.
                RedirectStandardOutput = true,
            };
            psi.ArgumentList.Add("--file-selection");
            psi.ArgumentList.Add("--title=Open a glTF model");
            psi.ArgumentList.Add("--file-filter=glTF models | *.glb *.gltf *.GLB *.GLTF");
            psi.ArgumentList.Add("--file-filter=All files | *");

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return null;
            }

            // Trim only the trailing newline: filenames may legally contain spaces.
            var path = proc.StandardOutput.ReadToEnd().TrimEnd('\r', '\n');
            proc.WaitForExit();
            return proc.ExitCode == 0 && path.Length > 0 ? path : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool IsOptionValue(string[] args, string value, params string[] valueOptions)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (valueOptions.Contains(args[i]) && args[i + 1] == value)
            {
                return true;
            }
        }

        return false;
    }

    private static void PrintSummary(string path, GltfModel model)
    {
        Console.WriteLine($"model: {path}");
        Console.WriteLine($"  generator:  {model.Generator ?? "(unknown)"}");
        Console.WriteLine($"  meshes:     {model.Meshes.Count} ({model.Meshes.Sum(m => m.Primitives.Count)} primitives)");
        Console.WriteLine($"  nodes:      {model.Nodes.Count}");
        Console.WriteLine($"  materials:  {model.Materials.Count}");
        Console.WriteLine($"  textures:   {model.Textures.Count}");
        Console.WriteLine($"  lights:     {model.Lights.Count}");
        Console.WriteLine($"  animations: {model.Animations.Count}");
        if (model.IgnoredExtensions.Count > 0)
        {
            Console.WriteLine($"  extensions not honoured (model loads regardless): {string.Join(", ", model.IgnoredExtensions)}");
        }
    }

    /// <summary>
    /// Offscreen render: hidden window, draw a few frames, read the framebuffer to a PNG.
    /// Used for verification without putting windows on any display.
    /// </summary>
    private static unsafe void RunCapture(string path, GltfModel model, string outputPath, int frames, int animIndex = 0, float time = 0f, bool animate = true)
    {
        var options = WindowOptions.Default with
        {
            Size = new Vector2D<int>(1280, 800),
            Title = "Centre Stage capture",
            API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(3, 3)),
            VSync = false,
            IsVisible = false,
        };

        var window = Window.Create(options);
        GL? gl = null;
        Renderer? renderer = null;
        ScenePose? pose = null;
        var camera = new OrbitCamera();
        var remaining = frames;

        window.Load += () =>
        {
            gl = window.CreateOpenGL();
            var decoded = Renderer.DecodeTextures(model.Textures);
            renderer = new Renderer(gl, model, decoded, animate);
            camera.FrameBounds(renderer.Bounds.min, renderer.Bounds.max);
            Console.WriteLine($"  bounds: ({renderer.Bounds.min:F2}) .. ({renderer.Bounds.max:F2})");
            Console.WriteLine($"  camera: pos=({camera.Position:F2}) dist={camera.Distance:F2}");

            if (animate && model.Animations.Count > 0)
            {
                pose = new ScenePose(model);
                var index = Math.Clamp(animIndex, 0, model.Animations.Count - 1);
                var clip = model.Animations[index];
                pose.Apply(clip, time);
                Console.WriteLine($"  pose: animation {index} \"{clip.Name ?? "(unnamed)"}\" at {time:F2}s / {clip.Duration:F2}s");
            }
        };

        window.Render += _ =>
        {
            if (gl is null || renderer is null)
            {
                return;
            }

            if (Environment.GetEnvironmentVariable("CENTRESTAGE_STATUS_TEST") == "1")
            {
                // Diagnostic: draw the model FIRST, then the overlay — reproducing the
                // close sequence, where the renderer's cull state is live when the
                // status text draws. Text must still be visible.
                var aspect = (float)window.Size.X / MathF.Max(window.Size.Y, 1);
                renderer.Draw(camera, aspect, model.Lights, new Vector3(0.45f, 0.55f, 0.70f), new Vector3(0.22f, 0.20f, 0.18f), pose);
                var tw = window.FramebufferSize.X;
                var th = window.FramebufferSize.Y;
                using var overlay = new StatusOverlay(gl);
                overlay.DrawStatus(tw, th, "Closing", "Releasing resources", -1f, 1.25f);
                if (model.Animations.Count > 0)
                {
                    // Same line the interactive viewer shows: proves the selector HUD draws.
                    overlay.DrawHud(tw, th, $"1/{model.Animations.Count}  \"Take 001\"  0.0s / 1.0s  playing", 17f,
                        new Vector4(0.80f, 0.85f, 0.93f, 0.92f));
                }
            }
            else
            {
                var aspect = (float)window.Size.X / MathF.Max(window.Size.Y, 1);
                var sky = new Vector3(0.45f, 0.55f, 0.70f);
                var ground = new Vector3(0.22f, 0.20f, 0.18f);
                renderer.Draw(camera, aspect, model.Lights, sky, ground, pose);
            }

            remaining--;
            if (remaining > 0)
            {
                return;
            }

            var w = (uint)window.FramebufferSize.X;
            var h = (uint)window.FramebufferSize.Y;
            var pixels = new byte[w * h * 4];
            fixed (byte* p = pixels)
            {
                gl.ReadPixels(0, 0, w, h, PixelFormat.Rgba, PixelType.UnsignedByte, p);
            }

            // GL reads bottom-up; PNG is top-down.
            var rowBytes = (int)(w * 4);
            var flipped = new byte[pixels.Length];
            for (var y = 0; y < h; y++)
            {
                Array.Copy(pixels, y * rowBytes, flipped, (h - 1 - y) * rowBytes, rowBytes);
            }

            using var stream = File.Create(outputPath);
            var writer = new StbImageWriteSharp.ImageWriter();
            writer.WritePng(flipped, (int)w, (int)h, StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha, stream);
            Console.WriteLine($"captured {w}x{h} -> {outputPath}");

            // Release GL resources while the context is still current, then close.
            renderer.Dispose();
            renderer = null;
            window.Close();
        };

        window.Closing += () =>
        {
            // Same teardown guarantee as the viewer: never touch GL after the
            // event loop has torn the context down.
            renderer?.Dispose();
            renderer = null;
        };

        window.Run();
        window.Dispose();
    }

    private enum LoadStage
    {
        Loading,
        Ready,
        Closing,
        Failed,
    }

    /// <summary>
    /// Interactive viewer. The window opens immediately and stays responsive: parsing and
    /// texture decoding run on a background thread (decoding dominates load time), textures
    /// upload incrementally a frame at a time, and every stage paints status text. GL
    /// objects are released in <see cref="IWindow.Closing"/> while the context is still
    /// current — deleting them after the event loop exits crashes the driver lookup.
    /// </summary>
    private static void RunViewer(string path, bool animate = true)
    {
        var options = WindowOptions.Default with
        {
            Size = new Vector2D<int>(1280, 800),
            Title = $"Centre Stage — loading {Path.GetFileName(path)}…",
            API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(3, 3)),
            VSync = true,
        };

        var window = Window.Create(options);
        GL? gl = null;
        Renderer? renderer = null;
        StatusOverlay? overlay = null;
        AnimationPlayback? playback = null;
        var camera = new OrbitCamera();

        // Load pipeline: parse + texture decode run on a background thread; decoded
        // images go into a bounded queue the render thread drains and uploads. Bounded
        // so a fast decoder can't hoard hundreds of MB of decoded pixels ahead of the GPU.
        GltfModel? loaded = null;
        string? loadError = null;
        var textureTotal = 0;
        var uploadDone = 0;
        var decodeFailed = 0;
        var stage = LoadStage.Loading;
        var closeRequested = false;
        var closingPainted = false;
        var loadFailed = false;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var textureQueue = new BlockingCollection<(int Index, ImageResult Image)>(8);

        var loadTask = Task.Run(() =>
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var model = GltfModel.Load(path);
                // Publish the count first: the render thread keys the status label on
                // `loaded`, and on weak memory models it must not see a zero total.
                textureTotal = model.Textures.Count;
                loaded = model;
                Console.WriteLine($"  parsed in {sw.ElapsedMilliseconds} ms");
                sw.Restart();

                Parallel.ForEach(
                    Enumerable.Range(0, textureTotal),
                    new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2) },
                    i =>
                    {
                        if (Renderer.DecodeTexture(model.Textures[i], i) is not { } image)
                        {
                            Interlocked.Increment(ref decodeFailed);
                            return;
                        }

                        try
                        {
                            // Blocks when the queue is full: decode stays a bounded
                            // distance ahead of GPU upload instead of racing it.
                            textureQueue.Add((i, image));
                        }
                        catch (InvalidOperationException)
                        {
                            // Queue completed during teardown (window closed mid-decode).
                        }
                    });
                Console.WriteLine($"  decoded {textureTotal} textures in {sw.ElapsedMilliseconds} ms");
            }
            finally
            {
                // Unblocks the consumer on success AND on a parse failure, so the
                // ready-condition can never wait on a producer that has stopped.
                textureQueue.CompleteAdding();
            }
        });

        // Observe a fault even if the user closes before the render loop reads it —
        // an unobserved task exception surfaces at finalisation otherwise.
        _ = loadTask.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);

        window.Load += () =>
        {
            gl = window.CreateOpenGL();
            overlay = new StatusOverlay(gl);
            HookInput(window, camera, () => closeRequested = true, () => playback);
        };

        window.Render += delta =>
        {
            if (gl is null || overlay is null)
            {
                return;
            }

            // Test hook: exercise the full open→load→close lifecycle unattended.
            if (int.TryParse(Environment.GetEnvironmentVariable("CENTRESTAGE_AUTO_CLOSE_MS"), out var autoCloseMs)
                && clock.ElapsedMilliseconds > autoCloseMs)
            {
                closeRequested = true;
            }

            var width = window.FramebufferSize.X;
            var height = window.FramebufferSize.Y;
            var elapsed = (float)clock.Elapsed.TotalSeconds;

            if (closeRequested && stage != LoadStage.Closing)
            {
                // Esc is honoured from every stage, including the error screen.
                stage = LoadStage.Closing;
            }

            if (stage == LoadStage.Failed)
            {
                gl.Viewport(0, 0, (uint)width, (uint)height);
                gl.ClearColor(0.12f, 0.08f, 0.10f, 1f);
                gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
                overlay.DrawStatus(width, height, "Load failed", Truncate(loadError, 68), 0f, elapsed);
                return;
            }

            if (stage == LoadStage.Closing)
            {
                gl.Viewport(0, 0, (uint)width, (uint)height);
                gl.ClearColor(0.10f, 0.11f, 0.13f, 1f);
                gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
                overlay.DrawStatus(width, height, "Closing", "Releasing resources", -1f, elapsed);
                if (closingPainted)
                {
                    // Second pass: the status frame has been presented; tear down now,
                    // while the context is still current.
                    CompleteQueue();
                    ReleaseGl(ref renderer, ref overlay);
                    window.Close();
                }

                closingPainted = true;
                return;
            }

            if (loadTask.IsFaulted)
            {
                loadError = loadTask.Exception?.GetBaseException().Message ?? "unknown error";
                Console.Error.WriteLine($"error: failed to load '{path}': {loadError}");
                loadFailed = true;
                stage = LoadStage.Failed;
                return;
            }

            if (stage == LoadStage.Loading)
            {
                gl.Viewport(0, 0, (uint)width, (uint)height);
                gl.ClearColor(0.10f, 0.11f, 0.13f, 1f);
                gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

                // Geometry uploads as soon as the parse lands (it is fast); textures
                // stream in from the decode queue a couple per frame.
                if (renderer is null && loaded is not null)
                {
                    renderer = new Renderer(gl, loaded, animate: animate);
                    playback = animate ? new AnimationPlayback(loaded) : null;
                    camera.FrameBounds(renderer.Bounds.min, renderer.Bounds.max);
                    window.Title = $"Centre Stage — {Path.GetFileName(path)}";
                }

                if (renderer is not null)
                {
                    for (var n = 0; n < 2 && textureQueue.TryTake(out var item); n++)
                    {
                        renderer.UploadTexture(item.Index, item.Image);
                        uploadDone++;
                    }

                    if (textureTotal > 0)
                    {
                        // Count failed decodes as processed: they never reach the queue,
                        // so the label would otherwise stall short of the total.
                        var handled = uploadDone + Volatile.Read(ref decodeFailed);
                        overlay.DrawStatus(width, height, "Loading",
                            $"Uploading textures {Math.Min(handled, textureTotal)}/{textureTotal}",
                            (float)handled / textureTotal, elapsed);
                    }
                    else
                    {
                        overlay.DrawStatus(width, height, "Loading", "Preparing", 1f, elapsed);
                    }

                    // Ready once the task has finished and the queue is fully drained
                    // (IsCompleted covers failed decodes, which never enter the queue).
                    if (loadTask.IsCompletedSuccessfully && textureQueue.IsCompleted)
                    {
                        stage = LoadStage.Ready;
                    }
                }
                else
                {
                    overlay.DrawStatus(width, height, "Loading", "Parsing model", -1f, elapsed);
                }

                return;
            }

            if (stage == LoadStage.Ready && renderer is not null && loaded is not null)
            {
                var aspect = (float)width / MathF.Max(height, 1);
                var sky = new Vector3(0.45f, 0.55f, 0.70f);
                var ground = new Vector3(0.22f, 0.20f, 0.18f);

                if (playback is not null && playback.HasAnimations)
                {
                    playback.Advance((float)delta);
                    playback.Apply();
                    renderer.Draw(camera, aspect, loaded.Lights, sky, ground, playback.Pose);
                    overlay.DrawHud(width, height, playback.Describe(), 17f,
                        new Vector4(0.80f, 0.85f, 0.93f, 0.92f));
                }
                else
                {
                    renderer.Draw(camera, aspect, loaded.Lights, sky, ground);
                }
            }
        };

        window.FramebufferResize += size =>
        {
            gl?.Viewport(size);
        };

        window.Closing += () =>
        {
            // Fired for the close button too, where no further frame can be drawn:
            // GL teardown must still happen before the context dies. Completing the
            // queue here also unblocks any decoder still waiting on a full queue.
            CompleteQueue();
            ReleaseGl(ref renderer, ref overlay);
        };

        window.Run();
        CompleteQueue();
        ReleaseGl(ref renderer, ref overlay);
        window.Dispose();

        // Match the CLI paths: a failed load exits 2 even in interactive mode.
        // The flag is sticky because the Closing stage overwrites the Failed state.
        if (loadFailed)
        {
            Environment.ExitCode = 2;
        }

        // Idempotent: a load failure completes the queue in the task's finally too.
        void CompleteQueue()
        {
            if (!textureQueue.IsAddingCompleted)
            {
                textureQueue.CompleteAdding();
            }
        }
    }

    /// <summary>Releases GL objects exactly once; safe to call from multiple teardown paths.</summary>
    private static void ReleaseGl(ref Renderer? renderer, ref StatusOverlay? overlay)
    {
        renderer?.Dispose();
        renderer = null;
        overlay?.Dispose();
        overlay = null;
    }

    private static string Truncate(string? text, int max)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return text.Length <= max ? text : text[..(max - 3)] + "...";
    }

    private static void HookInput(IWindow window, OrbitCamera camera, Action requestClose, Func<AnimationPlayback?>? playback = null)
    {
        var input = window.CreateInput();
        var getPlayback = playback;

        // Last non-fullscreen state, so F11 restores exactly what the user had
        // (maximized included) rather than always dropping to Normal.
        var windowedState = window.WindowState;

        void ToggleFullscreen()
        {
            if (window.WindowState == WindowState.Fullscreen)
            {
                window.WindowState = windowedState;
            }
            else
            {
                windowedState = window.WindowState;
                window.WindowState = WindowState.Fullscreen;
            }
        }

        // Keyboard first and unconditionally: Esc is the only close path that works
        // without a pointer device, so it must not be behind the mouse guard.
        foreach (var keyboard in input.Keyboards)
        {
            keyboard.KeyDown += (_, key, _) =>
            {
                if (key == Key.Escape)
                {
                    // Ask the render loop to paint a closing status first; window.Close()
                    // from here would skip straight to teardown.
                    requestClose();
                }
                else if (key == Key.F11)
                {
                    ToggleFullscreen();
                }
                else if (getPlayback?.Invoke() is { HasAnimations: true } active)
                {
                    switch (key)
                    {
                        case Key.Space:
                            active.Playing = !active.Playing;
                            break;
                        case Key.Right:
                        case Key.Down:
                            active.Next();
                            break;
                        case Key.Left:
                        case Key.Up:
                            active.Previous();
                            break;
                    }
                }
            };
        }

        var primary = input.Mice.FirstOrDefault();
        if (primary is null)
        {
            return;
        }

        var dragging = false;
        var panning = false;
        var lastPosition = new Vector2(float.NaN, float.NaN);
        var lastClickTicks = 0L;
        var lastClickPos = new Vector2(float.NaN, float.NaN);

        primary.MouseDown += (mouse, button) =>
        {
            dragging = button == MouseButton.Left;
            panning = button == MouseButton.Right || button == MouseButton.Middle;
            if (button == MouseButton.Left)
            {
                // Double-click toggles fullscreen, like F11. A third rapid click
                // starts a fresh pair rather than toggling straight back.
                var now = Environment.TickCount64;
                var pos = new Vector2(mouse.Position.X, mouse.Position.Y);
                if (now - lastClickTicks < 500 && Vector2.Distance(pos, lastClickPos) < 6f)
                {
                    lastClickTicks = 0;
                    ToggleFullscreen();
                }
                else
                {
                    lastClickTicks = now;
                    lastClickPos = pos;
                }
            }

            lastPosition = new Vector2(float.NaN, float.NaN); // reset so the first move doesn't jump
        };
        primary.MouseUp += (_, button) =>
        {
            if (button == MouseButton.Left)
            {
                dragging = false;
            }

            if (button is MouseButton.Right or MouseButton.Middle)
            {
                panning = false;
            }
        };
        primary.MouseMove += (_, position) =>
        {
            // Silk.NET reports absolute positions; orbit/pan need per-event deltas.
            var current = new Vector2(position.X, position.Y);
            if (float.IsNaN(lastPosition.X))
            {
                lastPosition = current;
                return;
            }

            var delta = current - lastPosition;
            lastPosition = current;

            if (dragging)
            {
                // Y is negated for the Exhibit-style convention: dragging up looks up.
                camera.Orbit(delta.X * 0.005f, -delta.Y * 0.005f);
            }
            else if (panning)
            {
                camera.Pan(-delta.X, delta.Y);
            }
        };
        primary.Scroll += (_, wheel) => camera.Zoom(wheel.Y * 0.25f);
    }
}
