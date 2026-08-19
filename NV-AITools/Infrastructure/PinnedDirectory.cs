using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace NVAITools.Infrastructure;

sealed class PinnedDirectory : IDisposable
{
    const uint FileFlagBackupSemantics = 0x02000000;
    const uint FileFlagOpenReparsePoint = 0x00200000;
    const uint GenericRead = 0x80000000;
    const int FileAttributeTagInfo = 9;

    readonly SafeFileHandle handle;

    public PinnedDirectory(string path, string? expectedFinalPath = null)
    {
        handle = CreateFile(
            path,
            GenericRead,
            FileShare.Read | FileShare.Write,
            IntPtr.Zero,
            FileMode.Open,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw ValidationFailure(path, new Win32Exception(error));
        }

        try
        {
            if (!GetFileInformationByHandleEx(
                    handle,
                    FileAttributeTagInfo,
                    out FileAttributeTagInformation attributes,
                    (uint)Marshal.SizeOf<FileAttributeTagInformation>()))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (((FileAttributes)attributes.FileAttributes & FileAttributes.ReparsePoint) != 0)
                throw new ToolException(
                    $"NV-AITools directory cannot be a filesystem reparse point: {path}",
                    ExitCodes.DependencyOrWorkspaceFailure);

            FinalPath = Path.TrimEndingDirectorySeparator(GetFinalPath(handle));
            if (expectedFinalPath is not null &&
                !FinalPath.Equals(
                    Path.TrimEndingDirectorySeparator(expectedFinalPath),
                    StringComparison.OrdinalIgnoreCase))
                throw new ToolException(
                    $"NV-AITools directory resolved outside its expected location: {path}",
                    ExitCodes.DependencyOrWorkspaceFailure);
        }
        catch (Exception exception) when (exception is IOException or Win32Exception)
        {
            handle.Dispose();
            throw ValidationFailure(path, exception);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public string FinalPath { get; }

    public void Dispose() => handle.Dispose();

    internal static string GetFinalPath(SafeFileHandle handle)
    {
        var path = new StringBuilder(512);
        uint length = GetFinalPathNameByHandle(handle, path, (uint)path.Capacity, 0);
        if (length == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (length < path.Capacity)
            return path.ToString();

        path.EnsureCapacity(checked((int)length + 1));
        length = GetFinalPathNameByHandle(handle, path, (uint)path.Capacity, 0);
        if (length == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (length >= path.Capacity)
            throw new IOException("The resolved path changed while it was being read.");
        return path.ToString();
    }

    static ToolException ValidationFailure(string path, Exception exception) => new(
        $"Could not validate NV-AITools directory '{path}': {exception.Message}",
        ExitCodes.DependencyOrWorkspaceFailure,
        exception);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        FileMode creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        int fileInformationClass,
        out FileAttributeTagInformation fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        StringBuilder filePath,
        uint filePathLength,
        uint flags);

    [StructLayout(LayoutKind.Sequential)]
    struct FileAttributeTagInformation
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }
}
