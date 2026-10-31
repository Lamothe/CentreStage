using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CentreStage;

/// <summary>Optional Linux FIFO for instance-scoped automation. The caller must
/// create it in a private directory with mode 0600. Commands are applied on the
/// render thread; the open FIFO accepts successive writers.</summary>
internal sealed class ControlChannel
{
    private const int OpenReadWrite = 2;
    private const int OpenNonBlock = 0x800;
    private const int OpenPath = 0x200000;
    private const int AtSymlinkNoFollow = 0x100;
    private const int OpenNoFollow = 0x20000;
    private const int OpenCloseOnExec = 0x80000;
    private const int AtEmptyPath = 0x1000;
    private const int SetFileStatusFlags = 4;
    private const uint StatxBasicStats = 0x7ff;
    private const ushort FileTypeMask = 0xf000;
    private const ushort FifoType = 0x1000;
    private const int MaxLineLength = 256;
    private readonly BlockingCollection<string> _commands = new(64);
    private readonly Thread _thread;

    // Linux statx has a fixed, architecture-independent 256-byte layout.
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct Statx
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(20)] public uint UserId;
        [FieldOffset(28)] public ushort Mode;
        [FieldOffset(32)] public ulong Inode;
        [FieldOffset(136)] public uint DeviceMajor;
        [FieldOffset(140)] public uint DeviceMinor;
    }

    [DllImport("libc", SetLastError = true, EntryPoint = "open")]
    private static extern int Open(string path, int flags);
    [DllImport("libc", SetLastError = true, EntryPoint = "statx")]
    private static extern int StatFile(int fd, string path, int flags, uint mask, out Statx stat);
    [DllImport("libc", SetLastError = true, EntryPoint = "fcntl")]
    private static extern int SetFlags(int fd, int command, int flags);
    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();

    private ControlChannel(string path)
    {
        _thread = new Thread(() => ReadLoop(path)) { IsBackground = true, Name = "control-fifo" };
        _thread.Start();
    }

    public static ControlChannel Start(string path) => new(path);

    public bool TryDequeue(out string? command) => _commands.TryTake(out command);

    internal bool WaitForExit(TimeSpan timeout) => _thread.Join(timeout);

    private void ReadLoop(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            Console.Error.WriteLine("error: control FIFO requires Linux");
            return;
        }
        while (true)
        {
            try
            {
                // O_PATH cannot activate a device. Validate the held inode
                // before reopening that exact inode through /proc/self/fd.
                var pathFd = Open(path, OpenPath | OpenNoFollow | OpenCloseOnExec);
                if (pathFd < 0)
                {
                    throw new IOException($"open path failed (errno {Marshal.GetLastPInvokeError()})");
                }
                using var pathHandle = new SafeFileHandle((IntPtr)pathFd, ownsHandle: true);
                if (StatFile(pathFd, "", AtEmptyPath | AtSymlinkNoFollow, StatxBasicStats, out var identity) != 0
                    || (identity.Mask & 0x10b) != 0x10b
                    || (identity.Mode & FileTypeMask) != FifoType || (identity.Mode & 0x1ff) != 0x180
                    || identity.UserId != GetEffectiveUserId())
                {
                    throw new IOException("expected an owner-only FIFO owned by the current user");
                }
                var fd = Open($"/proc/self/fd/{pathFd}", OpenReadWrite | OpenNonBlock | OpenCloseOnExec);
                if (fd < 0)
                {
                    throw new IOException($"open failed (errno {Marshal.GetLastPInvokeError()})");
                }
                using var handle = new SafeFileHandle((IntPtr)fd, ownsHandle: true);
                if (StatFile(fd, "", AtEmptyPath, StatxBasicStats, out var stat) != 0
                    || (stat.Mask & 0x3) != 0x3 || (stat.Mask & 0x8) == 0
                    || (stat.Mode & FileTypeMask) != FifoType || (stat.Mode & 0x1ff) != 0x180
                    || stat.UserId != GetEffectiveUserId() || stat.Inode != identity.Inode
                    || stat.DeviceMajor != identity.DeviceMajor || stat.DeviceMinor != identity.DeviceMinor)
                {
                    throw new IOException("expected an owner-only FIFO owned by the current user");
                }
                // Open non-blocking so the descriptor is validated before any
                // untrusted writer can make us block on the wrong object.
                // F_SETFL clears O_NONBLOCK; the handle remains the same FIFO.
                if (SetFlags(fd, SetFileStatusFlags, 0) != 0)
                {
                    throw new IOException($"fcntl failed (errno {Marshal.GetLastPInvokeError()})");
                }
                using var stream = new FileStream(handle, FileAccess.Read);
                using var reader = new StreamReader(stream);
                var line = new System.Text.StringBuilder();
                var oversized = false;
                int ch;
                while ((ch = reader.Read()) != -1)
                {
                    if (ch == '\n')
                    {
                        if (!oversized && line.ToString().Trim() is { Length: > 0 } command)
                        {
                            _commands.Add(command);
                        }
                        else if (oversized)
                        {
                            Console.Error.WriteLine("error: control fifo discarded an oversized line");
                        }
                        line.Clear();
                        oversized = false;
                    }
                    else if (!oversized)
                    {
                        if (line.Length == MaxLineLength)
                        {
                            oversized = true;
                        }
                        else
                        {
                            line.Append((char)ch);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"error: control fifo '{path}': {ex.Message}");
                return;
            }
        }
    }
}
