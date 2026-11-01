using System.Numerics;
using CentreStage.Core;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace CentreStage;

/// <summary>
/// Opt-in Centre Stage view of the face-trained renderer. The locked held-out face
/// frame is drawn twice at the capture raster inside Centre Stage — full scene shader
/// (the reference) and reduced shader (the cheap frame) — and only the cheap frame
/// goes through the trained CPU model. At the default window size the two panels are
/// shown 1:1, so what is on screen is the captured raster with no resampling; a
/// smaller window reports the size it actually used. The model is specific to this
/// framing, light and asset; this is not a real-time upscaler and not DLSS 5.
/// </summary>
internal static class FaceNeuralDemo
{
    // Fixed reporting window over the head and hair of the locked frame, measured once
    // from its silhouette. It is a reporting region, not a segmentation: the same
    // rectangle is used for every number quoted. The face-only box is reported in the
    // run log as well, because the hair is high-contrast and lifts the head average.
    private const int HeadX = 170;
    private const int HeadY = 10;
    private const int HeadWidth = 350;
    private const int HeadHeight = 450;
    private const int FaceX = 208;
    private const int FaceY = 192;
    private const int FaceWidth = 240;
    private const int FaceHeight = 258;

    // 120x120 crop of the face, shown at 200 px (1.67x) to make the local difference
    // readable without smoothing it away.
    private const int ZoomX = 250;
    private const int ZoomY = 240;
    private const int ZoomSize = 120;
    private const int ZoomDisplay = 200;

    private sealed record Panel(uint Texture, string Caption, string Metric);

    public static void Run(string modelPath, string checkpointPath)
    {
        try
        {
            RunCore(modelPath, checkpointPath);
        }
        catch (DllNotFoundException ex)
        {
            Console.Error.WriteLine($"face-neural: native scene host unavailable: {ex.Message}");
            Console.Error.WriteLine("face-neural: run linux/build-scene-host.sh or set CENTRESTAGE_SCENE_LIBRARY");
            Environment.ExitCode = 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"face-neural: {ex.Message}");
            Environment.ExitCode = 1;
        }
    }

    private static unsafe void RunCore(string modelPath, string checkpointPath)
    {
        ScenePairCapture.RejectDiagnosticVariables();
        var asset = Path.GetFullPath(modelPath);
        ScenePairCapture.VerifySnapshot(asset);

        var locked = ScenePairCapture.FaceTestView;
        var size = locked.Size;
        var model = GltfModel.Load(asset);
        var decoded = Renderer.DecodeTextures(model.Textures);
        if (decoded.Any(image => image is null))
        {
            throw new InvalidDataException("one or more model textures could not be decoded");
        }

        var options = WindowOptions.Default with
        {
            Size = new Vector2D<int>(1400, 1100),
            Title = "Centre Stage — face close-up: full render vs scene-trained neural output",
            API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core,
                ContextFlags.ForwardCompatible, new APIVersion(3, 3)),
            VSync = true,
        };
        if (Environment.GetEnvironmentVariable("CENTRESTAGE_WINDOW_POS")?.Split(',') is [var xs, var ys]
            && int.TryParse(xs, out var wx) && int.TryParse(ys, out var wy))
        {
            options = options with { Position = new Vector2D<int>(wx, wy) };
        }

        var window = Window.Create(options);
        GL? gl = null;
        Renderer? fullRenderer = null;
        Renderer? cheapRenderer = null;
        SceneNeuralModel? neuralModel = null;
        StatusOverlay? overlay = null;
        IInputContext? inputContext = null;
        uint framebuffer = 0;
        uint colourTexture = 0;
        uint depthBuffer = 0;
        uint referenceTexture = 0;
        uint cheapTexture = 0;
        uint neuralTexture = 0;
        uint presentProgram = 0;
        uint presentVao = 0;
        var neuralOnLeft = false;
        var closing = false;
        var framesRendered = 0;
        var cheapHead = 0.0;
        var neuralHead = 0.0;
        var cheapWhole = 0.0;
        var neuralWhole = 0.0;
        var cheapFace = 0.0;
        var neuralFace = 0.0;

        try
        {
            window.Load += () =>
            {
                gl = window.CreateOpenGL();
                overlay = new StatusOverlay(gl);
                // UploadTextures consumes the array slots, so the cheap renderer gets the
                // only clone; the original goes to the full renderer.
                var cheapDecoded = (StbImageSharp.ImageResult?[])decoded.Clone();
                fullRenderer = new Renderer(gl, model, decoded, animate: false, cheapScene: false);
                cheapRenderer = new Renderer(gl, model, cheapDecoded, animate: false, cheapScene: true);
                if (fullRenderer.Bounds != cheapRenderer.Bounds)
                {
                    throw new InvalidOperationException("renderer geometry bounds differ");
                }
                neuralModel = new SceneNeuralModel(checkpointPath);

                colourTexture = gl.GenTexture();
                gl.BindTexture(TextureTarget.Texture2D, colourTexture);
                gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)size, (uint)size,
                    0, PixelFormat.Rgba, PixelType.UnsignedByte, (void*)0);
                PreviewGl.SetNearest(gl);
                depthBuffer = gl.GenRenderbuffer();
                gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, depthBuffer);
                gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.DepthComponent24,
                    (uint)size, (uint)size);
                framebuffer = gl.GenFramebuffer();
                gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
                gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                    TextureTarget.Texture2D, colourTexture, 0);
                gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
                    RenderbufferTarget.Renderbuffer, depthBuffer);
                var status = gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
                gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
                if (status != GLEnum.FramebufferComplete)
                {
                    throw new InvalidOperationException($"face capture framebuffer incomplete: {status}");
                }

                var camera = new OrbitCamera
                {
                    Yaw = locked.Yaw,
                    Pitch = locked.Pitch,
                };
                camera.SetFrameAspect(1f);
                camera.Focus(locked.Focus, locked.Distance);
                RuntimeLight[] lights =
                [
                    new RuntimeLight
                    {
                        Type = LightType.Directional,
                        Direction = locked.LightDirection,
                        Color = locked.Colour,
                        Intensity = locked.LightIntensity,
                    },
                ];

                var cheapRgb = Capture(cheapRenderer, camera, lights, locked.Sky, locked.Ground);
                var referenceRgb = Capture(fullRenderer, camera, lights, locked.Sky, locked.Ground);
                var neuralRgb = neuralModel.Infer(cheapRgb, size, size);

                cheapWhole = PreviewGl.Psnr(cheapRgb, referenceRgb);
                neuralWhole = PreviewGl.Psnr(neuralRgb, referenceRgb);
                cheapHead = PreviewGl.PsnrRegion(cheapRgb, referenceRgb, size, size, HeadX, HeadY,
                    HeadWidth, HeadHeight);
                neuralHead = PreviewGl.PsnrRegion(neuralRgb, referenceRgb, size, size, HeadX, HeadY,
                    HeadWidth, HeadHeight);
                cheapFace = PreviewGl.PsnrRegion(cheapRgb, referenceRgb, size, size, FaceX, FaceY,
                    FaceWidth, FaceHeight);
                neuralFace = PreviewGl.PsnrRegion(neuralRgb, referenceRgb, size, size, FaceX, FaceY,
                    FaceWidth, FaceHeight);
                if (Environment.GetEnvironmentVariable("CENTRESTAGE_SCENE_DUMP") is { Length: > 0 } dumpDirectory)
                {
                    Directory.CreateDirectory(dumpDirectory);
                    PreviewGl.WritePpm(Path.Combine(dumpDirectory, "face-reference.ppm"), referenceRgb, size, size);
                    PreviewGl.WritePpm(Path.Combine(dumpDirectory, "face-cheap.ppm"), cheapRgb, size, size);
                    PreviewGl.WritePpm(Path.Combine(dumpDirectory, "face-neural.ppm"), neuralRgb, size, size);
                    Console.WriteLine($"face-neural: wrote frames to {Path.GetFullPath(dumpDirectory)}");
                }
                referenceTexture = PreviewGl.Upload(gl, referenceRgb, size, size);
                cheapTexture = PreviewGl.Upload(gl, cheapRgb, size, size);
                neuralTexture = PreviewGl.Upload(gl, neuralRgb, size, size);
                presentProgram = PreviewGl.BuildPresentProgram(gl);
                presentVao = gl.GenVertexArray();

                Console.WriteLine($"face-neural: host {neuralModel.Library}");
                Console.WriteLine($"face-neural: label {SceneNeuralModel.Describe()}");
                Console.WriteLine($"face-neural: checkpoint {checkpointPath}");
                Console.WriteLine($"face-neural: frame {size}x{size} yaw {locked.Yaw:F2} pitch {locked.Pitch:F2} (held out)");
                Console.WriteLine($"face-neural: whole frame cheap {cheapWhole:F2} dB, neural {neuralWhole:F2} dB");
                Console.WriteLine($"face-neural: head region cheap {cheapHead:F2} dB, neural {neuralHead:F2} dB");
                Console.WriteLine($"face-neural: face region cheap {cheapFace:F2} dB, neural {neuralFace:F2} dB");
                Console.WriteLine($"face-neural: head delta {neuralHead - cheapHead:+0.00;-0.00} dB");
                if (neuralWhole < cheapWhole)
                {
                    Console.Error.WriteLine(
                        "face-neural: warning: the model is worse than the cheap frame here; check that the checkpoint was trained on this framing");
                }

                byte[] Capture(Renderer renderer, OrbitCamera cam, RuntimeLight[] lts, Vector3 skyColour,
                    Vector3 groundColour)
                {
                    gl!.Viewport(0, 0, (uint)size, (uint)size);
                    gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
                    renderer.Draw(cam, 1f, lts, skyColour, groundColour);
                    gl.Finish();
                    var error = gl.GetError();
                    if (error != GLEnum.NoError)
                    {
                        throw new InvalidOperationException($"GL error during face capture: {error}");
                    }
                    var rgba = new byte[size * size * 4];
                    gl.ReadPixels(0, 0, (uint)size, (uint)size, PixelFormat.Rgba,
                        PixelType.UnsignedByte, rgba);
                    gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
                    var rgb = new byte[size * size * 3];
                    for (var row = 0; row < size; row++)
                    {
                        for (var x = 0; x < size; x++)
                        {
                            // GL's origin is bottom-left; images and the model are top-down.
                            var src = ((size - 1 - row) * size + x) * 4;
                            var dst = (row * size + x) * 3;
                            rgb[dst] = rgba[src];
                            rgb[dst + 1] = rgba[src + 1];
                            rgb[dst + 2] = rgba[src + 2];
                        }
                    }
                    return rgb;
                }
            };

            window.Render += _ =>
            {
                if (gl is null || overlay is null || presentProgram == 0)
                {
                    return;
                }

                int width = window.FramebufferSize.X;
                int height = window.FramebufferSize.Y;
                gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
                gl.Viewport(0, 0, (uint)Math.Max(width, 1), (uint)Math.Max(height, 1));
                gl.ClearColor(0.02f, 0.025f, 0.035f, 1f);
                gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
                gl.Disable(EnableCap.DepthTest);
                gl.Disable(EnableCap.CullFace);
                gl.Disable(EnableCap.Blend);

                const int margin = 40;
                var panel = Math.Min(size, (width - 3 * margin) / 2);
                const int panelTop = 84;
                var zoomTop = panelTop + panel + 80;
                var zoom = Math.Min(ZoomDisplay, height - zoomTop - 96);
                var showPanels = panel >= 64;
                var showZoom = showPanels && zoom >= 64;
                var thumb = Math.Min(zoom, ZoomDisplay);
                var panelIsOneToOne = panel == size;
                var zoomRatio = "";

                var referencePanel = new Panel(referenceTexture,
                    "FULL DEFINITION — full scene shader",
                    panelIsOneToOne ? $"{size}x{size}, 1:1, reference"
                        : $"{size}x{size}, shown at {panel} px, reference");
                var neuralPanel = new Panel(neuralTexture,
                    "NEURAL — cheap frame through the trained model",
                    $"head PSNR {neuralHead:F2} dB vs cheap {cheapHead:F2} dB");
                Panel[] panels = neuralOnLeft ? [neuralPanel, referencePanel] : [referencePanel, neuralPanel];

                // Quads first: the text overlay binds its own program, VAO and viewport.
                if (showPanels)
                {
                    for (var i = 0; i < panels.Length; i++)
                    {
                        PreviewGl.DrawQuad(gl, panels[i].Texture, presentProgram, presentVao,
                            margin + i * (panel + margin), height - panelTop - panel, panel,
                            Vector2.Zero, Vector2.One);
                    }
                }
                if (showZoom)
                {
                    // The presenter's top edge samples uUvMin.y, so these are top-down
                    // fractions of the image, as in SceneNeuralDemo's crop constants.
                    var zoomMin = new Vector2((float)ZoomX / size, (float)ZoomY / size);
                    var zoomMax = new Vector2((float)(ZoomX + ZoomSize) / size,
                        (float)(ZoomY + ZoomSize) / size);
                    var zoomFactor = (float)thumb / ZoomSize;
                    PreviewGl.DrawQuad(gl, cheapTexture, presentProgram, presentVao, margin,
                        height - zoomTop - thumb, thumb, zoomMin, zoomMax);
                    PreviewGl.DrawQuad(gl, referenceTexture, presentProgram, presentVao,
                        margin + thumb + margin, height - zoomTop - thumb, thumb, zoomMin, zoomMax);
                    PreviewGl.DrawQuad(gl, neuralTexture, presentProgram, presentVao,
                        margin + 2 * (thumb + margin), height - zoomTop - thumb, thumb, zoomMin, zoomMax);
                    zoomRatio = $"{zoomFactor:F2}x";
                }

                gl.Viewport(0, 0, (uint)Math.Max(width, 1), (uint)Math.Max(height, 1));

                if (showPanels)
                {
                    for (var i = 0; i < panels.Length; i++)
                    {
                        var x = margin + i * (panel + margin);
                        overlay.DrawTextAt(width, height, panels[i].Caption, x, panelTop + panel + 10,
                            18f, new Vector4(0.9f, 0.93f, 1f, 1f));
                        overlay.DrawTextAt(width, height, panels[i].Metric, x, panelTop + panel + 36,
                            15f, new Vector4(0.7f, 0.76f, 0.86f, 1f));
                    }
                }
                if (showZoom)
                {
                    var labels = new[] { "CHEAP INPUT", $"REFERENCE {zoomRatio}", $"NEURAL {zoomRatio}" };
                    for (var i = 0; i < labels.Length; i++)
                    {
                        overlay.DrawTextAt(width, height, labels[i], margin + i * (thumb + margin),
                            zoomTop + thumb + 6, 14f, new Vector4(0.62f, 0.68f, 0.78f, 1f));
                    }
                    var textX = margin + 3 * (thumb + margin);
                    var textWidth = width - textX - margin;
                    if (textWidth > 320)
                    {
                        var improvement = neuralHead >= cheapHead
                            ? $"Head PSNR improves {neuralHead - cheapHead:F2} dB on this held-out frame."
                            : $"Head PSNR is worse by {cheapHead - neuralHead:F2} dB on this held-out frame.";
                        var lines = new[]
                        {
                            $"Held-out frame: yaw {locked.Yaw:F2}, pitch {locked.Pitch:F2}, light and pose not used in training.",
                            $"Head {cheapHead:F2} -> {neuralHead:F2} dB; face box {cheapFace:F2} -> {neuralFace:F2} dB.",
                            $"{improvement} Whole frame {cheapWhole:F2} -> {neuralWhole:F2} dB.",
                            "Fine detail is not recovered; the offline detail proxy regresses slightly.",
                            "Scene-trained neural rendering demonstration - not DLSS 5.",
                        };
                        for (var i = 0; i < lines.Length; i++)
                        {
                            overlay.DrawTextAt(width, height, lines[i], textX, zoomTop + i * 22, 14f,
                                i == lines.Length - 1
                                    ? new Vector4(0.95f, 0.8f, 0.4f, 1f)
                                    : new Vector4(0.72f, 0.78f, 0.88f, 1f));
                        }
                    }
                }

                overlay.DrawTextAt(width, height, "Face close-up: Centre Stage render vs trained neural output",
                    margin, 16f, 23f, new Vector4(1f, 0.86f, 0.38f, 1f));
                overlay.DrawTextAt(width, height,
                    $"One locked, held-out face view rendered twice at {size}x{size}: full shader and reduced shader. " +
                    $"Only the reduced frame goes to the model; its output is the panel marked NEURAL (F10 swaps sides).",
                    margin, 48f, 14f, new Vector4(0.78f, 0.83f, 0.9f, 1f));
                overlay.DrawTextAt(width, height,
                    panelIsOneToOne
                        ? "F10 swaps the panels | Esc closes | panels are 1:1, no upscaling"
                        : $"F10 swaps the panels | Esc closes | panels are {panel} px, not 1:1",
                    margin, height - 26f, 14f, new Vector4(0.62f, 0.68f, 0.78f, 1f));

                if (Environment.GetEnvironmentVariable("CENTRESTAGE_WINDOW_DUMP") is { Length: > 0 } windowDump
                    && ++framesRendered == 3)
                {
                    try
                    {
                        if (width < 1 || height < 1)
                        {
                            throw new InvalidOperationException(
                                $"framebuffer is {width}x{height}, not ready to capture");
                        }
                        var rgba = new byte[width * height * 4];
                        gl.ReadPixels(0, 0, (uint)width, (uint)height,
                            PixelFormat.Rgba, PixelType.UnsignedByte, rgba);
                        var rgb = new byte[width * height * 3];
                        for (var row = 0; row < height; row++)
                        {
                            for (var x = 0; x < width; x++)
                            {
                                var src = ((height - 1 - row) * width + x) * 4;
                                var dst = (row * width + x) * 3;
                                rgb[dst] = rgba[src];
                                rgb[dst + 1] = rgba[src + 1];
                                rgb[dst + 2] = rgba[src + 2];
                            }
                        }
                        var fullPath = Path.GetFullPath(windowDump);
                        var directory = Path.GetDirectoryName(fullPath);
                        if (!string.IsNullOrEmpty(directory))
                        {
                            Directory.CreateDirectory(directory);
                        }
                        PreviewGl.WritePpm(fullPath, rgb, width, height);
                        Console.WriteLine($"face-neural: wrote window to {fullPath}");
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"face-neural: window dump failed: {ex.Message}");
                        Environment.ExitCode = 1;
                    }
                    closing = true;
                }

                if (closing)
                {
                    window.Close();
                }
            };

            window.Load += () =>
            {
                inputContext = window.CreateInput();
                foreach (var keyboard in inputContext.Keyboards)
                {
                    keyboard.KeyDown += (_, key, _) =>
                    {
                        if (key == Key.Escape)
                        {
                            closing = true;
                        }
                        else if (key == Key.F10)
                        {
                            neuralOnLeft = !neuralOnLeft;
                        }
                    };
                }
            };

            window.Closing += () =>
            {
                inputContext?.Dispose();
                if (gl is not null)
                {
                    cheapRenderer?.Dispose();
                    fullRenderer?.Dispose();
                    overlay?.Dispose();
                    neuralModel?.Dispose();
                    if (presentProgram != 0) gl.DeleteProgram(presentProgram);
                    if (presentVao != 0) gl.DeleteVertexArray(presentVao);
                    if (neuralTexture != 0) gl.DeleteTexture(neuralTexture);
                    if (cheapTexture != 0) gl.DeleteTexture(cheapTexture);
                    if (referenceTexture != 0) gl.DeleteTexture(referenceTexture);
                    if (framebuffer != 0) gl.DeleteFramebuffer(framebuffer);
                    if (colourTexture != 0) gl.DeleteTexture(colourTexture);
                    if (depthBuffer != 0) gl.DeleteRenderbuffer(depthBuffer);
                }
            };

            window.Run();
        }
        finally
        {
            window.Dispose();
        }
    }
}