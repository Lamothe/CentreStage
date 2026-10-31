using System.ComponentModel;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CentreStage.Core;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace CentreStage;

/// <summary>Fixed, split-by-camera-and-lighting, same-extent scene capture.</summary>
internal static class ScenePairCapture
{
    private const int Width = 256;
    private const int Height = 256;
    private const string AssetHash = "ab34f516d6bb8d337e7607c337d1158726b46e15b2880391f7d48bb76b83f22a";
    private static readonly string[] DiagnosticVariables =
    [
        "CENTRESTAGE_SOLID", "CENTRESTAGE_WIREFRAME", "CENTRESTAGE_STATUS_TEST",
        "CENTRESTAGE_AUTO_CLOSE_MS", "CENTRESTAGE_SYNTH_CLICK", "CENTRESTAGE_SYNTH_MENU",
        "CENTRESTAGE_SYNTH_MENU_SHOT", "CENTRESTAGE_CTL_FIFO", "CENTRESTAGE_DLSS_SYNTHETIC",
        "CENTRESTAGE_DLSS_EXTREME", "CENTRESTAGE_DLSS_FAULT"
    ];

    // Entire camera trajectories and lighting conditions are disjoint across splits.
    // Angles are absolute radians, not interpolated or randomly sampled.
    private sealed record Group(string Split, float[] Yaws, float Pitch, Vector3 Direction,
        Vector3 Colour, float Intensity, Vector3 Sky, Vector3 Ground);
    private static readonly Group[] Groups =
    [
        new("train", [-1.35f, -1.15f, -0.95f, -0.75f, -0.55f, -0.35f, -0.15f, 0.05f], -0.45f,
            new(-2, -4, -2), new(1.0f, 0.95f, 0.9f), 2.5f, new(0.45f, 0.55f, 0.70f), new(0.22f, 0.20f, 0.18f)),
        new("val", [0.35f, 0.55f], -0.35f,
            new(3, -5, -1), new(0.8f, 0.9f, 1.0f), 2.0f, new(0.40f, 0.50f, 0.65f), new(0.20f, 0.18f, 0.16f)),
        new("test", [0.85f, 1.05f], -0.25f,
            new(-1, -3, 4), new(1.0f, 0.85f, 0.7f), 3.0f, new(0.50f, 0.48f, 0.55f), new(0.18f, 0.17f, 0.20f))
    ];

    [DllImport("libc", SetLastError = true, EntryPoint = "mkdir")]
    private static extern int Mkdir(string path, uint mode);

    public static void Run(string[] args)
    {
        try
        {
            if (args.Length != 3 || args[1] != "--scene-pairs" || args[0].StartsWith('-') || args[2].StartsWith('-'))
            {
                throw new ArgumentException("usage: CentreStage <snapshot.glb> --scene-pairs <new-directory>");
            }
            foreach (var name in DiagnosticVariables)
            {
                if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)))
                {
                    throw new InvalidOperationException($"unset diagnostic variable {name} before capture");
                }
            }
            if (!OperatingSystem.IsLinux())
            {
                throw new PlatformNotSupportedException("exclusive capture-directory creation requires Linux");
            }
            var asset = Path.GetFullPath(args[0]);
            var output = Path.GetFullPath(args[2]);
            var hash = HashFile(asset);
            if (!string.Equals(hash, AssetHash, StringComparison.Ordinal))
            {
                throw new InvalidDataException("asset is not the specified Teen Kitty snapshot (SHA-256 mismatch)");
            }
            // mkdir(2), unlike Directory.CreateDirectory, is atomic and rejects all existing
            // entries, including dangling symlinks. Parent must already exist.
            if (Mkdir(output, Convert.ToUInt32("700", 8)) != 0)
            {
                throw new IOException($"cannot exclusively create '{output}': {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
            }
            Capture(asset, hash, output);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"scene-pairs: {ex.Message}");
            Environment.ExitCode = 1;
        }
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static unsafe void Capture(string asset, string assetHash, string output)
    {
        var model = GltfModel.Load(asset);
        if (HashFile(asset) != assetHash)
        {
            throw new IOException("snapshot changed during loading");
        }
        var frames = Groups.SelectMany(g => g.Yaws.Select((yaw, i) => (group: g, yaw, index: i))).ToArray();
        var records = new List<object>();
        var manifests = Groups.ToDictionary(g => g.Split, _ => new StringBuilder());
        var options = WindowOptions.Default with
        {
            Size = new Vector2D<int>(Width, Height), Title = "Centre Stage scene pairs",
            API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(3, 3)),
            VSync = false, IsVisible = false, ShouldSwapAutomatically = false,
        };
        using var window = Window.Create(options);
        GL? gl = null;
        Renderer? full = null;
        Renderer? cheap = null;
        var camera = new OrbitCamera();
        var next = 0;
        string? graphics = null;
        window.Load += () =>
        {
            gl = window.CreateOpenGL();
            if (window.FramebufferSize.X != Width || window.FramebufferSize.Y != Height)
            {
                throw new InvalidOperationException($"framebuffer is {window.FramebufferSize.X}x{window.FramebufferSize.Y}, expected {Width}x{Height}");
            }
            graphics = $"{Marshal.PtrToStringAnsi((nint)gl.GetString(StringName.Vendor))} | {Marshal.PtrToStringAnsi((nint)gl.GetString(StringName.Renderer))} | {Marshal.PtrToStringAnsi((nint)gl.GetString(StringName.Version))}";
            var decoded = Renderer.DecodeTextures(model.Textures);
            if (decoded.Any(image => image is null))
            {
                throw new InvalidDataException("one or more model textures could not be decoded");
            }
            // UploadTextures consumes the array slots. Clone the references before the
            // first constructor so both renderers receive the identical decoded images.
            var cheapDecoded = (StbImageSharp.ImageResult?[])decoded.Clone();
            full = new Renderer(gl, model, decoded, animate: false, cheapScene: false);
            cheap = new Renderer(gl, model, cheapDecoded, animate: false, cheapScene: true);
            if (full.Bounds != cheap.Bounds)
            {
                throw new InvalidOperationException("renderer geometry bounds differ");
            }
            camera.SetFrameAspect((float)Width / Height);
            // Fit for each view to avoid clipping; each pair uses exactly the same camera.
            gl.Viewport(0, 0, Width, Height);
        };
        window.Render += _ =>
        {
            if (gl is null || full is null || cheap is null)
            {
                throw new InvalidOperationException("renderers not ready");
            }
            if (window.FramebufferSize.X != Width || window.FramebufferSize.Y != Height)
            {
                throw new InvalidOperationException("framebuffer size changed during capture");
            }
            var frame = frames[next];
            var g = frame.group;
            camera.Yaw = frame.yaw;
            camera.Pitch = g.Pitch;
            camera.FrameBounds(full.Bounds.min, full.Bounds.max);
            var direction = Vector3.Normalize(g.Direction);
            RuntimeLight[] lights = [new RuntimeLight { Type = LightType.Directional, Direction = direction, Color = g.Colour, Intensity = g.Intensity }];
            var stem = $"{g.Split}-{frame.index:D3}";
            var cheapName = $"{stem}-cheap.ppm";
            var fullName = $"{stem}-full.ppm";
            var cheapFrame = DrawAndSave(cheap, cheapName);
            var fullFrame = DrawAndSave(full, fullName);
            var (maxDiff, meanDiff) = Diff(cheapFrame.Rgb, fullFrame.Rgb);
            // A bit-identical pair means the cheap change had no effect on this
            // asset and the pair is an identity control, not training data.
            if (maxDiff == 0)
            {
                throw new InvalidOperationException(
                    $"cheap render is bit-identical to full render at {stem}; the cheap shader change is not effective");
            }
            manifests[g.Split].Append(cheapName).Append('\t').Append(fullName).Append('\n');
            records.Add(new
            {
                split = g.Split, index = frame.index, cheap = cheapName, full = fullName,
                cheapSha256 = cheapFrame.Hash, fullSha256 = fullFrame.Hash,
                cheapRenderMs = cheapFrame.DrawMs, fullRenderMs = fullFrame.DrawMs,
                cheapFullMaxAbsDiff = maxDiff, cheapFullMeanAbsDiff = meanDiff,
                yawRadians = camera.Yaw, pitchRadians = camera.Pitch, distance = camera.Distance,
                target = Vec(camera.Target), position = Vec(camera.Position), fieldOfViewRadians = camera.FieldOfView,
                near = camera.Near, far = camera.Far,
                lighting = new { direction = Vec(direction), colour = Vec(g.Colour), intensity = g.Intensity, sky = Vec(g.Sky), ground = Vec(g.Ground) }
            });
            Console.WriteLine($"scene-pairs: {stem}");
            next++;
            if (next == frames.Length)
            {
                window.Close();
            }

            (string Hash, byte[] Rgb, double DrawMs) DrawAndSave(Renderer renderer, string name)
            {
                // Wall-clock milliseconds around Draw + gl.Finish at 256x256: honest
                // measurement of this run, noisy at this scale, not real-time evidence.
                var stopwatch = Stopwatch.StartNew();
                renderer.Draw(camera, (float)Width / Height, lights, g.Sky, g.Ground, null);
                gl.Finish();
                stopwatch.Stop();
                var error = gl.GetError();
                if (error != GLEnum.NoError)
                {
                    throw new InvalidOperationException($"GL error drawing {name}: {error}");
                }
                var rgba = new byte[Width * Height * 4];
                fixed (byte* p = rgba)
                {
                    gl.ReadPixels(0, 0, Width, Height, PixelFormat.Rgba, PixelType.UnsignedByte, p);
                }
                error = gl.GetError();
                if (error != GLEnum.NoError)
                {
                    throw new InvalidOperationException($"GL error reading {name}: {error}");
                }
                var rgb = new byte[Width * Height * 3];
                for (var y = 0; y < Height; y++)
                {
                    for (var x = 0; x < Width; x++)
                    {
                        var src = ((Height - 1 - y) * Width + x) * 4;
                        var dst = (y * Width + x) * 3;
                        Array.Copy(rgba, src, rgb, dst, 3);
                    }
                }
                var path = Path.Combine(output, name);
                using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    file.Write(Encoding.ASCII.GetBytes($"P6\n{Width} {Height}\n255\n"));
                    file.Write(rgb);
                }
                return (HashFile(path), rgb, stopwatch.Elapsed.TotalMilliseconds);
            }
        };
        window.Closing += () =>
        {
            cheap?.Dispose();
            cheap = null;
            full?.Dispose();
            full = null;
        };
        window.Run();
        if (next != frames.Length)
        {
            throw new IOException($"capture stopped after {next} of {frames.Length} pairs");
        }
        if (HashFile(asset) != assetHash)
        {
            throw new IOException("snapshot changed during capture");
        }
        foreach (var (split, contents) in manifests)
        {
            File.WriteAllText(Path.Combine(output, $"{split}.tsv"), contents.ToString(), new UTF8Encoding(false));
        }
        var metadata = new
        {
            schema = "centrestage-scene-pairs-v2",
            label = "scene-training pairs for a neural rendering demonstration (not DLSS5)",
            asset, assetSha256 = assetHash,
            width = Width, height = Height, format = "P6 RGB8 top-down", pose = "bind pose; animation disabled",
            renderer = graphics, lightPolicy = "one declared directional light per split; asset lights replaced",
            cheapPass = "reduced texture/lighting fidelity: base colour sampled at +2.0 mip LOD bias, auxiliary material maps not sampled (factors only), Lambert diffuse + hemispheric ambient + factor-only emissive; GGX specular, clearcoat, sheen, transmission and normal-map perturbation omitted",
            timingNote = "cheapRenderMs/fullRenderMs are wall-clock milliseconds around Draw + gl.Finish at this resolution on this driver; noisy, not real-time evidence",
            differenceNote = "cheapFullMaxAbsDiff/cheapFullMeanAbsDiff compare the two rasters per pair; a bit-identical pair aborts the capture",
            frames = records
        };
        // Completion marker written last; incomplete runs must never be consumed.
        var json = JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true });
        var temporaryMetadata = Path.Combine(output, "metadata.json.tmp");
        File.WriteAllText(temporaryMetadata, json + "\n", new UTF8Encoding(false));
        File.Move(temporaryMetadata, Path.Combine(output, "metadata.json"), overwrite: false);
        Console.WriteLine($"scene-pairs: complete: {output} (neural rendering demonstration data, not DLSS5)");
    }

    private static (int MaxAbsDiff, double MeanAbsDiff) Diff(byte[] a, byte[] b)
    {
        if (a.Length != b.Length)
        {
            throw new ArgumentException("raster lengths differ");
        }
        var max = 0;
        long sum = 0;
        for (var i = 0; i < a.Length; i++)
        {
            var d = Math.Abs(a[i] - b[i]);
            if (d > max)
            {
                max = d;
            }
            sum += d;
        }
        return (max, a.Length == 0 ? 0 : (double)sum / a.Length);
    }

    private static float[] Vec(Vector3 v) => [v.X, v.Y, v.Z];
}
