using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SystemWatch.Management;

[SupportedOSPlatform("linux")]
internal static class LinuxDirectorySync
{
    internal static void Flush(string directory)
    {
        const int oDirectory = 0x10000, oNoFollow = 0x20000, oCloExec = 0x80000;
        var fd = Native.open(directory, oDirectory | oNoFollow | oCloExec);
        if (fd < 0) throw new IOException("cannot open backup directory for sync",
            new Win32Exception(Marshal.GetLastPInvokeError()));
        try
        {
            if (Native.fsync(fd) != 0)
                throw new IOException("backup directory sync failed",
                    new Win32Exception(Marshal.GetLastPInvokeError()));
        }
        finally { Native.close(fd); }
    }

    private static class Native
    {
        #pragma warning disable SYSLIB1054 // Fixed Linux libc directory fsync.
        [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
        internal static extern int open(string path, int flags);
        [DllImport("libc", SetLastError = true)]
        internal static extern int fsync(int fd);
        [DllImport("libc", SetLastError = true)]
        internal static extern int close(int fd);
        #pragma warning restore SYSLIB1054
    }
}
