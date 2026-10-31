using System.Diagnostics;
using System.Runtime.Versioning;
using CentreStage;

namespace CentreStage.Core.Tests;

public sealed class ControlChannelTests
{
    [Fact]
    public async Task PrivateFifosIsolateInstancesAndAcceptSuccessiveWriters()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        var directory = CreateDirectory();
        var firstPath = Path.Combine(directory, "first");
        var secondPath = Path.Combine(directory, "second");
        try
        {
            Mkfifo(firstPath, "600");
            Mkfifo(secondPath, "600");
            var first = ControlChannel.Start(firstPath);
            var second = ControlChannel.Start(secondPath);
            await Write(firstPath, "orbit 1 2\n");
            Assert.Equal("orbit 1 2", await Take(first));
            Assert.False(second.TryDequeue(out _));
            await Write(firstPath, "shot ../bad\n");
            Assert.Equal("shot ../bad", await Take(first));
            await Write(secondPath, "zoom 1\n");
            Assert.Equal("zoom 1", await Take(second));
            Assert.False(first.TryDequeue(out _));
            await Write(firstPath, new string('x', 257) + "\nframe\n");
            Assert.Equal("frame", await Take(first));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task RejectsRegularFilesAndInsecureFifos()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        var directory = CreateDirectory();
        try
        {
            var file = Path.Combine(directory, "file");
            await File.WriteAllTextAsync(file, "frame\n");
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var regular = ControlChannel.Start(file);
            var fifo = Path.Combine(directory, "insecure");
            Mkfifo(fifo, "666");
            var insecure = ControlChannel.Start(fifo);
            var target = Path.Combine(directory, "target");
            Mkfifo(target, "600");
            var link = Path.Combine(directory, "link");
            File.CreateSymbolicLink(link, target);
            var symlink = ControlChannel.Start(link);
            var device = ControlChannel.Start("/dev/null");
            Assert.True(regular.WaitForExit(TimeSpan.FromSeconds(3)), "regular file was not rejected");
            Assert.True(insecure.WaitForExit(TimeSpan.FromSeconds(3)), "insecure FIFO was not rejected");
            Assert.True(symlink.WaitForExit(TimeSpan.FromSeconds(3)), "symlink was not rejected");
            Assert.True(device.WaitForExit(TimeSpan.FromSeconds(3)), "device was not rejected");
            Assert.False(regular.TryDequeue(out _));
            Assert.False(insecure.TryDequeue(out _));
            Assert.False(symlink.TryDequeue(out _));
            Assert.False(device.TryDequeue(out _));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [SupportedOSPlatform("linux")]
    private static string CreateDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"centrestage-ctl-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static void Mkfifo(string path, string mode)
    {
        using var process = Process.Start(new ProcessStartInfo("mkfifo")
        {
            ArgumentList = { "-m", mode, path },
            UseShellExecute = false,
        })!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    private static async Task Write(string path, string content)
    {
        // Once open, the channel keeps its read end open for subsequent writers.
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Task.Run(async () => await File.WriteAllTextAsync(path, content, cancellation.Token), cancellation.Token)
            .WaitAsync(cancellation.Token);
    }

    private static async Task<string?> Take(ControlChannel channel)
    {
        for (var i = 0; i < 100; i++)
        {
            if (channel.TryDequeue(out var command))
            {
                return command;
            }
            await Task.Delay(20);
        }
        throw new TimeoutException("Control command was not received");
    }
}
