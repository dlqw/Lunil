using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Lunil.Hosting;

internal static class LuaPatchDurableFileSystem
{
    private const uint MoveFileReplaceExisting = 0x1;
    private const uint MoveFileWriteThrough = 0x8;

    public static void ReplaceFile(string source, string destination)
    {
        if (LunilOperatingSystem.IsWindows())
        {
            if (!WindowsMoveFileEx(
                    source,
                    destination,
                    MoveFileReplaceExisting | MoveFileWriteThrough))
            {
                throw new IOException(
                    $"Windows atomic replace failed for '{destination}'.",
                    new Win32Exception(LunilMarshal.GetLastPInvokeError()));
            }

            return;
        }

        if (UnixRename(source, destination) != 0)
        {
            throw NativeIoException("rename", destination);
        }
    }

    public static void FlushDirectory(string directory)
    {
        if (LunilOperatingSystem.IsWindows())
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
        var error = LunilMarshal.GetLastPInvokeError();
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

    [DllImport("libc", EntryPoint = "rename", SetLastError = true, ExactSpelling = true)]
    private static extern int UnixRename(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string source,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string destination);

    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WindowsMoveFileEx(
        string existingFileName,
        string newFileName,
        uint flags);
#pragma warning restore SYSLIB1054
#pragma warning restore CA2101
}
