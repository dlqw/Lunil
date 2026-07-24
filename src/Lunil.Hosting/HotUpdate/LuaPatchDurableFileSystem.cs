using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Lunil.Hosting;

internal static class LuaPatchDurableFileSystem
{
    public static void FlushDirectory(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            // .NET does not expose opening a Windows directory with backup semantics. Callers
            // flush the replaced file before atomic same-volume rename; NTFS/ReFS own the final
            // directory-entry durability boundary.
            return;
        }

        var descriptor = UnixOpen(directory, 0);
        if (descriptor < 0)
        {
            throw NativeIoException("open", directory);
        }

        try
        {
            if (UnixFsync(descriptor) != 0)
            {
                throw NativeIoException("fsync", directory);
            }
        }
        finally
        {
            _ = UnixClose(descriptor);
        }
    }

    private static IOException NativeIoException(string operation, string path)
    {
        var error = Marshal.GetLastPInvokeError();
        return new IOException(
            $"Unix {operation} failed for durable directory '{path}'.",
            new Win32Exception(error));
    }

#pragma warning disable CA2101 // Unix open requires an explicitly marshalled UTF-8 path.
#pragma warning disable SYSLIB1054 // DllImport avoids enabling unsafe code for libc calls.
    [DllImport("libc", EntryPoint = "open", SetLastError = true, ExactSpelling = true)]
    private static extern int UnixOpen(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags);

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true, ExactSpelling = true)]
    private static extern int UnixFsync(int descriptor);

    [DllImport("libc", EntryPoint = "close", ExactSpelling = true)]
    private static extern int UnixClose(int descriptor);
#pragma warning restore SYSLIB1054
#pragma warning restore CA2101
}
