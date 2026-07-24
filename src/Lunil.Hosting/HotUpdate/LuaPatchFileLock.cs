using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Lunil.Hosting;

internal static class LuaPatchFileLock
{
    public static FileStream? TryOpenExclusive(
        string path,
        out IOException? exception,
        out bool contention)
    {
        try
        {
            var stream = new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.Read,
                bufferSize: 1,
                FileOptions.WriteThrough);
            if (!OperatingSystem.IsWindows())
            {
                var descriptor = stream.SafeFileHandle.DangerousGetHandle().ToInt32();
                if (UnixFlock(descriptor, UnixLockExclusive | UnixLockNonBlocking) != 0)
                {
                    var error = Marshal.GetLastPInvokeError();
                    exception = new IOException(
                        $"Unix flock failed for '{path}'.",
                        new Win32Exception(error));
                    contention = error is 11 or 35;
                    stream.Dispose();
                    return null;
                }
            }

            exception = null;
            contention = false;
            return stream;
        }
        catch (Exception caught) when (caught is IOException or UnauthorizedAccessException)
        {
            exception = caught as IOException ?? new IOException(caught.Message, caught);
            contention = caught is IOException ioException &&
                (ioException.HResult & 0xFFFF) is 11 or 32 or 33 or 35;
            return null;
        }
    }

#pragma warning disable SYSLIB1054 // DllImport avoids enabling unsafe code for libc flock.
    [DllImport("libc", EntryPoint = "flock", SetLastError = true, ExactSpelling = true)]
    private static extern int UnixFlock(int descriptor, int operation);
#pragma warning restore SYSLIB1054

    private const int UnixLockExclusive = 2;
    private const int UnixLockNonBlocking = 4;
}
