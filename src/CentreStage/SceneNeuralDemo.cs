using System.Numerics;
using CentreStage.Core;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace CentreStage;

/// <summary>
/// Opt-in Centre Stage view of the scene-trained renderer. The locked test view is
/// drawn twice at 256x256 — full shader (the reference both variants are scored
/// against) and reduced shader (the cheap frame) — and only the cheap frame goes
/// through the trained CPU model. The three frames are drawn with nearest filtering,
/// upscaled to fit, with each variant's PSNR against the reference. The model is
/// specific to this scene and pose; this is not a real-time upscaler and not DLSS 5.
/// </summary>
internal static class SceneNeuralDemo
{
    private const int CaptureSize = 256;

    // Square crop of the trained frame: head, shoulders and upper torso.
    private static readonly Vector2 ZoomMin = new(0.34f, 0.06f);
    private static readonly Vector2 ZoomMax = new(0.66f, 0.38f);

    private sealed record Panel(uint Texture, string Caption, string Metric);

    public static void Run(string modelPath, string checkpointPath)
    {
        try
        {
            RunCore(modelPath, checkpointPath);
        }
        catch (DllNotFoundException ex)
        {
            Console.Error.WriteLine($"scene-neural: native scene host unavailable: {ex.Message}");
            Console.Error.WriteLine("scene-neural: run linux/build-scene-host.sh or set CENTRESTAGE_SCENE_LIBRARY");
            Environment.ExitCode = 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"scene-neural: {ex.Message}");
            Environment.ExitCode = 1;
        }
    }

    private static unsafe void RunCore(string modelPath, string checkpointPath)
    {
        ScenePairCapture.RejectDiagnosticVariables();
        var asset = Path.GetFullPath(modelPath);
        ScenePairCapture.VerifySnapshot(asset);

        var model = GltfModel.Load(asset);
        var decoded = Renderer.DecodeTextures(model.Textures);
        if (decoded.Any(image => image is null))
        {
            throw new InvalidDataException("one or more model textures could not be decoded");
        }

        var options = WindowOptions.Default with
        {
            Size = new Vector2D<int>(1320, 772),
            Title = "Centre Stage — scene-trained neural rendering demo",
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
        var neuralAdjacentLeft = false;
        var closing = false;
        var cheapPsnr = 0.0;
        var neuralPsnr = 0.0;
        var framesRendered = 0;

        try
        {
            window.Load += () =>
            {
                gl = window.CreateOpenGL();
                overlay = new StatusOverlay(gl);
                // UploadTextures consumes the array slots, so the cheap renderer gets the
                // only clone; the original goes to the full renderer and is then empty.
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
                gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8,
                    CaptureSize, CaptureSize, 0, PixelFormat.Rgba, PixelType.UnsignedByte, (void*)0);
                PreviewGl.SetNearest(gl);
                depthBuffer = gl.GenRenderbuffer();
                gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, depthBuffer);
                gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.DepthComponent24,
                    CaptureSize, CaptureSize);
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
                    throw new InvalidOperationException($"scene capture framebuffer incomplete: {status}");
                }

                // Reproduce the locked test-000 view and light from the trained capture split.
                var camera = new OrbitCamera { Yaw = 0.85f, Pitch = -0.25f };
                camera.SetFrameAspect(1f);
                camera.FrameBounds(fullRenderer.Bounds.min, fullRenderer.Bounds.max);
                camera.Target = new Vector3(0f, 0.79002947f, 0f);
                camera.Distance = 2.1381934f;
                camera.Near = 0.0021382f;
                camera.Far = 100f;
                RuntimeLight[] lights =
                [
                    new RuntimeLight
                    {
                        Type = LightType.Directional,
                        Direction = Vector3.Normalize(new Vector3(-1f, -3f, 4f)),
                        Color = new Vector3(1f, 0.85f, 0.7f),
                        Intensity = 3f,
                    },
                ];
                var sky = new Vector3(0.5f, 0.48f, 0.55f);
                var ground = new Vector3(0.18f, 0.17f, 0.2f);

                var cheapRgb = Capture(cheapRenderer, camera, lights, sky, ground);
                var referenceRgb = Capture(fullRenderer, camera, lights, sky, ground);
                var neuralRgb = neuralModel.Infer(cheapRgb, CaptureSize, CaptureSize);

                cheapPsnr = PreviewGl.Psnr(cheapRgb, referenceRgb);
                neuralPsnr = PreviewGl.Psnr(neuralRgb, referenceRgb);
                if (Environment.GetEnvironmentVariable("CENTRESTAGE_SCENE_DUMP") is { Length: > 0 } dumpDirectory)
                {
                    Directory.CreateDirectory(dumpDirectory);
                    PreviewGl.WritePpm(Path.Combine(dumpDirectory, "reference.ppm"), referenceRgb, CaptureSize, CaptureSize);
                    PreviewGl.WritePpm(Path.Combine(dumpDirectory, "cheap.ppm"), cheapRgb, CaptureSize, CaptureSize);
                    PreviewGl.WritePpm(Path.Combine(dumpDirectory, "neural.ppm"), neuralRgb, CaptureSize, CaptureSize);
                    Console.WriteLine($"scene-neural: wrote frames to {Path.GetFullPath(dumpDirectory)}");
                }
                referenceTexture = PreviewGl.Upload(gl, referenceRgb, CaptureSize, CaptureSize);
                cheapTexture = PreviewGl.Upload(gl, cheapRgb, CaptureSize, CaptureSize);
                neuralTexture = PreviewGl.Upload(gl, neuralRgb, CaptureSize, CaptureSize);
                presentProgram = PreviewGl.BuildPresentProgram(gl);
                presentVao = gl.GenVertexArray();

                Console.WriteLine($"scene-neural: host {neuralModel.Library}");
                Console.WriteLine($"scene-neural: label {SceneNeuralModel.Describe()}");
                Console.WriteLine($"scene-neural: checkpoint {checkpointPath}");
                Console.WriteLine($"scene-neural: cheap vs reference PSNR {cheapPsnr:F2} dB");
                Console.WriteLine($"scene-neural: neural vs reference PSNR {neuralPsnr:F2} dB");
                Console.WriteLine($"scene-neural: delta {neuralPsnr - cheapPsnr:+0.00;-0.00} dB (single locked frame)");

                byte[] Capture(Renderer renderer, OrbitCamera cam, RuntimeLight[] lts, Vector3 skyColour,
                    Vector3 groundColour)
                {
                    gl!.Viewport(0, 0, CaptureSize, CaptureSize);
                    gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
                    renderer.Draw(cam, 1f, lts, skyColour, groundColour);
                    gl.Finish();
                    var error = gl.GetError();
                    if (error != GLEnum.NoError)
                    {
                        throw new InvalidOperationException($"GL error during scene capture: {error}");
                    }
                    var rgba = new byte[CaptureSize * CaptureSize * 4];
                    gl.ReadPixels(0, 0, CaptureSize, CaptureSize, PixelFormat.Rgba,
                        PixelType.UnsignedByte, rgba);
                    gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
                    var rgb = new byte[CaptureSize * CaptureSize * 3];
                    for (var row = 0; row < CaptureSize; row++)
                    {
                        for (var x = 0; x < CaptureSize; x++)
                        {
                            // GL's origin is bottom-left; images and the model are top-down.
                            var src = ((CaptureSize - 1 - row) * CaptureSize + x) * 4;
                            var dst = (row * CaptureSize + x) * 3;
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
                gl.ClearColor(0.035f, 0.045f, 0.065f, 1f);
                gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
                gl.Disable(EnableCap.DepthTest);
                gl.Disable(EnableCap.CullFace);
                gl.Disable(EnableCap.Blend);

                var margin = 24;
                var panel = Math.Min((width - 5 * margin) / 3, 400);
                var panelTop = 74;
                // The reference is always first; F10 swaps the cheap and neural panels.
                var cheapPanel = new Panel(cheapTexture, "CHEAP RENDER - reduced shader",
                    $"PSNR {cheapPsnr:F2} dB");
                var neuralPanel = new Panel(neuralTexture, "NEURAL OUTPUT - cheap frame + model",
                    $"PSNR {neuralPsnr:F2} dB");
                Panel[] panels =
                [
                    new Panel(referenceTexture, "REFERENCE - full scene shader", "target for both"),
                    neuralAdjacentLeft ? neuralPanel : cheapPanel,
                    neuralAdjacentLeft ? cheapPanel : neuralPanel,
                ];

                var zoomTop = panelTop + panel + 58;
                var zoom = Math.Min(160, height - zoomTop - 92);
                var showPanels = panel >= 48;
                var showZoom = showPanels && zoom >= 48;

                // Quads first: the text overlay binds its own program, VAO and viewport.
                if (showPanels)
                {
                    for (var i = 0; i < panels.Length; i++)
                    {
                        PreviewGl.DrawQuad(gl, panels[i].Texture, presentProgram, presentVao,
                            PanelX(i, width, panel, margin), height - panelTop - panel, panel,
                            Vector2.Zero, Vector2.One);
                    }
                }
                if (showZoom)
                {
                    for (var i = 0; i < panels.Length; i++)
                    {
                        PreviewGl.DrawQuad(gl, panels[i].Texture, presentProgram, presentVao,
                            PanelX(i, width, panel, margin), height - zoomTop - zoom, zoom,
                            ZoomMin, ZoomMax);
                    }
                }

                gl.Viewport(0, 0, (uint)Math.Max(width, 1), (uint)Math.Max(height, 1));

                if (showPanels)
                {
                    var cropPixels = (ZoomMax.X - ZoomMin.X) * CaptureSize;
                    for (var i = 0; i < panels.Length; i++)
                    {
                        var x = PanelX(i, width, panel, margin);
                        overlay.DrawTextAt(width, height, panels[i].Caption, x, panelTop + panel + 8,
                            16f, new Vector4(0.88f, 0.92f, 1f, 1f));
                        overlay.DrawTextAt(width, height, panels[i].Metric, x, panelTop + panel + 30,
                            16f, new Vector4(0.7f, 0.76f, 0.86f, 1f));
                        if (showZoom)
                        {
                            overlay.DrawTextAt(width, height, $"{zoom / cropPixels:F1}x crop", x,
                                zoomTop + zoom + 6, 14f, new Vector4(0.62f, 0.68f, 0.78f, 1f));
                        }
                    }
                }

                overlay.DrawTextAt(width, height, "Scene-trained neural rendering demo", 24f, 14f, 21f,
                    new Vector4(1f, 0.86f, 0.38f, 1f));
                overlay.DrawTextAt(width, height,
                    "The same locked test view drawn twice inside Centre Stage at 256x256: full shader (reference) and reduced shader (cheap).",
                    24f, 42f, 15f, new Vector4(0.78f, 0.83f, 0.9f, 1f));
                overlay.DrawTextAt(width, height,
                    "Only the cheap frame goes to the trained model. All three are 256x256 - none is upscaled. | F10 swaps | Esc closes",
                    24f, height - 28f, 14f, new Vector4(0.62f, 0.68f, 0.78f, 1f));

                // Evidence hook: one PPM of the composed window, after a couple of frames
                // so the text is laid out, then close. No portal or compositor involved.
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
                        Console.WriteLine($"scene-neural: wrote window to {fullPath}");
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"scene-neural: window dump failed: {ex.Message}");
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
                            neuralAdjacentLeft = !neuralAdjacentLeft;
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

    /// <summary>Left edge of panel <paramref name="index"/> in framebuffer pixels.</summary>
    private static int PanelX(int index, int width, int panel, int margin)
    {
        var first = (width - (3 * panel + 2 * margin)) / 2;
        return first + index * (panel + margin);
    }
}
