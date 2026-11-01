using System.Reflection;
using System.Runtime.InteropServices;

namespace CentreStage;

/// <summary>
/// The assembly's single DllImport resolver. .NET permits exactly one resolver per
/// assembly, and both native seams live here, so registration is centralised and the
/// library name selects the candidate paths: explicit environment variable, then the
/// runtimes folder next to the app binary, then normal probing.
/// </summary>
internal static class NativeLibraryLoader
{
    private const string DlssLibrary = "dlss5_nr_host";
    private const string SceneLibrary = "scene_host";
    private static readonly object Gate = new();
    private static bool _registered;
    private static string? _scenePath;

    internal static void EnsureRegistered()
    {
        lock (Gate)
        {
            if (_registered)
            {
                return;
            }
            NativeLibrary.SetDllImportResolver(typeof(NativeLibraryLoader).Assembly, Resolve);
            _registered = true;
        }
    }

    /// <summary>
    /// Resolves, load-tests and pins the scene host so the path reported to the user is
    /// the file the imports really load — a set-but-unusable
    /// CENTRESTAGE_SCENE_LIBRARY must not silently become a different build.
    /// </summary>
    internal static string SceneLibraryPath()
    {
        lock (Gate)
        {
            if (_scenePath is not null)
            {
                return _scenePath;
            }

            var explicitPath = Environment.GetEnvironmentVariable("CENTRESTAGE_SCENE_LIBRARY");
            var candidate = !string.IsNullOrWhiteSpace(explicitPath)
                ? explicitPath
                : Path.Combine(AppContext.BaseDirectory, "runtimes", "linux-x64", "native",
                    "libscene_host.so");
            if (!File.Exists(candidate))
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(explicitPath)
                    ? $"scene host library not found at '{candidate}': run linux/build-scene-host.sh, or set CENTRESTAGE_SCENE_LIBRARY"
                    : $"CENTRESTAGE_SCENE_LIBRARY is set to '{candidate}' but no such file exists");
            }
            if (!NativeLibrary.TryLoad(candidate, out _))
            {
                throw new InvalidOperationException(
                    $"cannot load scene host library '{candidate}' (missing dependencies?)");
            }

            _scenePath = candidate;
            return candidate;
        }
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        return name switch
        {
            DlssLibrary => Probe("CENTRESTAGE_DLSS_LIB", "libdlss5_nr_host.so", assembly, searchPath),
            SceneLibrary => ProbeScene(assembly, searchPath),
            _ => IntPtr.Zero,
        };
    }

    private static IntPtr ProbeScene(Assembly assembly, DllImportSearchPath? searchPath)
    {
        string? pinned;
        lock (Gate)
        {
            pinned = _scenePath;
        }
        if (pinned is not null)
        {
            return NativeLibrary.TryLoad(pinned, out var handle) ? handle : IntPtr.Zero;
        }

        return Probe("CENTRESTAGE_SCENE_LIBRARY", "libscene_host.so", assembly, searchPath);
    }

    private static IntPtr Probe(string environmentVariable, string fileName, Assembly assembly,
        DllImportSearchPath? searchPath)
    {
        var explicitPath = Environment.GetEnvironmentVariable(environmentVariable);
        if (!string.IsNullOrWhiteSpace(explicitPath) && NativeLibrary.TryLoad(explicitPath, out var fromEnvironment))
        {
            return fromEnvironment;
        }

        var local = Path.Combine(AppContext.BaseDirectory, "runtimes", "linux-x64", "native", fileName);
        if (NativeLibrary.TryLoad(local, out var fromRuntime))
        {
            return fromRuntime;
        }

        return NativeLibrary.TryLoad(fileName, assembly, searchPath, out var probed) ? probed : IntPtr.Zero;
    }
}