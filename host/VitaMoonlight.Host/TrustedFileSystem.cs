using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace VitaMoonlight.Host;

internal readonly record struct TrustedFileIdentity(
    uint VolumeSerialNumber,
    uint FileIndexHigh,
    uint FileIndexLow);

internal sealed class TrustedDirectoryLease : IDisposable
{
    private SafeFileHandle? handle;

    internal TrustedDirectoryLease(
        SafeFileHandle handle,
        TrustedFileIdentity identity)
    {
        this.handle = handle;
        Identity = identity;
    }

    internal TrustedFileIdentity Identity { get; }

    public void Dispose()
    {
        handle?.Dispose();
        handle = null;
    }
}

internal sealed record ProtectedDirectoryReplacement(
    TrustedDirectoryLease Lease,
    string? RetainedQuarantinePath);

/// <summary>
/// Performs privileged file operations through no-follow handles. The few
/// directories accepted by this type have trusted parents (Program Files or
/// the system-drive root); callers must secure the directory before using a
/// child.
/// </summary>
internal static class TrustedFileSystem
{
    private const uint ReadControl = 0x00020000;
    private const uint WriteDac = 0x00040000;
    private const uint WriteOwner = 0x00080000;
    private const uint Delete = 0x00010000;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint OwnerSecurityInformation = 0x00000001;
    private const uint DaclSecurityInformation = 0x00000004;
    private const uint ProtectedDaclSecurityInformation = 0x80000000;
    private const int SecurityDescriptorRevision = 1;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorAccessDenied = 5;
    private const int ErrorSharingViolation = 32;
    private const int ErrorDirectoryNotEmpty = 145;
    private const int ErrorAlreadyExists = 183;
    private const int FileDispositionInfo = 4;
    private const int FileRenameInfo = 3;

    private const string ProtectedDirectorySddl =
        "O:BAG:SYD:P" +
        "(A;OICI;FA;;;SY)" +
        "(A;OICI;FA;;;BA)" +
        "(A;OICI;GRGX;;;BU)";

    private const string ProtectedFileSddl =
        "O:BAG:SYD:P" +
        "(A;;FA;;;SY)" +
        "(A;;FA;;;BA)" +
        "(A;;GR;;;BU)";

    internal static TrustedDirectoryLease AcquireDirectoryLease(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Protected directory identities require Windows.");
        }

        var fullPath = Path.GetFullPath(path);
        var handle = OpenExisting(
            fullPath,
            FileReadAttributes,
            FileShare.Read,
            expectDirectory: true);
        try
        {
            var identity = Inspect(
                handle,
                fullPath,
                expectDirectory: true);
            return new TrustedDirectoryLease(handle, identity);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Replaces a fixed directory name without ever trusting or repairing an
    /// object that was already present there. An existing entry is renamed by
    /// a no-follow DELETE handle, and a directory born with the exact protected
    /// security descriptor is atomically renamed into the fixed name.
    /// </summary>
    internal static ProtectedDirectoryReplacement
        ReplaceWithNewProtectedDirectory(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Atomic protected directory replacement requires Windows.");
        }

        var fullPath = Path.GetFullPath(path)
            .TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        var parent = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidDataException(
                $"The protected directory has no parent: {fullPath}");
        var leaf = Path.GetFileName(fullPath);

        SafeFileHandle? detachedHandle = null;
        string? quarantinePath = null;
        SafeFileHandle? stagingHandle = null;
        string? stagingPath = null;

        try
        {
            try
            {
                detachedHandle = TryOpenExistingEntry(
                    fullPath,
                    Delete | FileReadAttributes,
                    FileShare.Read);
            }
            catch (Win32Exception error) when (
                error.NativeErrorCode is ErrorAccessDenied or
                    ErrorSharingViolation)
            {
                throw new InvalidOperationException(
                    $"The existing {fullPath} entry could not be safely " +
                    "detached. Restart Windows, then run Install/update " +
                    "display driver again. The host did not read or modify " +
                    "the existing entry.",
                    error);
            }

            if (detachedHandle is not null)
            {
                var candidateQuarantinePath = Path.Combine(
                    parent,
                    $".{leaf}.vita-moonlight-quarantine-" +
                    $"{Guid.NewGuid():N}");
                RenameByHandle(
                    detachedHandle,
                    candidateQuarantinePath,
                    "legacy virtual-display directory");
                quarantinePath = candidateQuarantinePath;
            }

            for (var attempt = 0; attempt < 4; attempt++)
            {
                stagingPath = Path.Combine(
                    parent,
                    $".{leaf}.vita-moonlight-create-{Guid.NewGuid():N}");
                try
                {
                    CreateProtectedDirectoryExact(stagingPath);
                    break;
                }
                catch (Win32Exception error)
                    when (error.NativeErrorCode == ErrorAlreadyExists)
                {
                    stagingPath = null;
                }
            }
            if (stagingPath is null)
            {
                throw new IOException(
                    $"Windows could not reserve a protected staging name " +
                    $"for {fullPath}.");
            }

            stagingHandle = OpenExisting(
                stagingPath,
                Delete | FileReadAttributes,
                FileShare.Read,
                expectDirectory: true);
            var createdIdentity = Inspect(
                stagingHandle,
                stagingPath,
                expectDirectory: true);
            try
            {
                RenameByHandle(
                    stagingHandle,
                    fullPath,
                    "new protected virtual-display directory");
            }
            catch (Exception error)
                when (error is Win32Exception or IOException)
            {
                TryDeleteEntryByHandle(stagingHandle);
                throw new InvalidOperationException(
                    $"The fixed path {fullPath} changed while its protected " +
                    "replacement was being installed. Restart Windows, then " +
                    "open Display & recovery and choose Repair Vita display driver again.",
                    error);
            }
            finally
            {
                stagingHandle.Dispose();
                stagingHandle = null;
            }

            var lease = AcquireDirectoryLease(fullPath);
            if (lease.Identity != createdIdentity)
            {
                lease.Dispose();
                throw new InvalidDataException(
                    $"The fixed path {fullPath} changed immediately after " +
                    "its protected replacement was installed. No display " +
                    "driver operation was started.");
            }

            if (detachedHandle is not null &&
                TryDeleteEntryByHandle(detachedHandle))
            {
                quarantinePath = null;
            }

            return new ProtectedDirectoryReplacement(
                lease,
                quarantinePath);
        }
        catch (Exception error) when (quarantinePath is not null)
        {
            throw new InvalidOperationException(
                $"The previous virtual-display directory was safely detached " +
                $"to {quarantinePath}, but the protected replacement could " +
                "not be completed. Restart Windows and run Install/update " +
                "display driver again. The detached contents were not read.",
                error);
        }
        finally
        {
            stagingHandle?.Dispose();
            detachedHandle?.Dispose();

            // A staging object is deleted only through the exact handle held
            // above. Never clean up by a pathname that another process could
            // replace after a failed transition.
        }
    }

    internal static void SecureDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(fullPath);
        if (!OperatingSystem.IsWindows()) return;

        using var handle = OpenExisting(
            fullPath,
            ReadControl | WriteDac | WriteOwner | FileReadAttributes,
            FileShare.Read,
            expectDirectory: true);
        var identity = Inspect(handle, fullPath, expectDirectory: true);
        ApplyExactSecurity(
            handle,
            ProtectedDirectorySddl,
            fullPath);

        // Reopen by name and compare stable file identity. This detects a path
        // substitution during the ACL transition instead of securing one
        // directory and subsequently operating through another.
        using var reopened = OpenExisting(
            fullPath,
            FileReadAttributes,
            FileShare.Read,
            expectDirectory: true);
        var reopenedIdentity = Inspect(
            reopened,
            fullPath,
            expectDirectory: true);
        if (identity != reopenedIdentity)
        {
            throw new InvalidDataException(
                $"The directory changed while it was being secured: {fullPath}");
        }
    }

    internal static void SecureExistingFile(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var handle = TryOpenExisting(
            Path.GetFullPath(path),
            ReadControl | WriteDac | WriteOwner | FileReadAttributes,
            FileShare.Read,
            expectDirectory: false);
        if (handle is null) return;
        Inspect(handle, path, expectDirectory: false);
        ApplyExactSecurity(
            handle,
            ProtectedFileSddl,
            path);
    }

    internal static string ReadAllText(string path)
    {
        if (!OperatingSystem.IsWindows()) return File.ReadAllText(path);
        using var handle = OpenExisting(
            Path.GetFullPath(path),
            GenericRead | FileReadAttributes,
            FileShare.Read,
            expectDirectory: false);
        Inspect(handle, path, expectDirectory: false);
        using var stream = new FileStream(handle, FileAccess.Read);
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    internal static byte[] ReadAllBytes(string path)
    {
        if (!OperatingSystem.IsWindows()) return File.ReadAllBytes(path);
        using var handle = OpenExisting(
            Path.GetFullPath(path),
            GenericRead | FileReadAttributes,
            FileShare.Read,
            expectDirectory: false);
        Inspect(handle, path, expectDirectory: false);
        using var stream = new FileStream(handle, FileAccess.Read);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    internal static void WriteAllText(
        string path,
        string content)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.WriteAllText(path, content);
            return;
        }

        var fullPath = Path.GetFullPath(path);
        using var handle = OpenOrCreateFile(
            fullPath,
            GenericRead | GenericWrite | ReadControl | WriteDac |
            WriteOwner | FileReadAttributes,
            FileShare.Read);
        Inspect(handle, fullPath, expectDirectory: false);
        ApplyExactSecurity(
            handle,
            ProtectedFileSddl,
            fullPath);
        using var stream = new FileStream(handle, FileAccess.ReadWrite);
        stream.SetLength(0);
        using var writer = new StreamWriter(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            leaveOpen: true);
        writer.Write(content);
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    internal static void AppendAllText(
        string path,
        string content)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.AppendAllText(path, content);
            return;
        }

        var fullPath = Path.GetFullPath(path);
        using var handle = OpenOrCreateFile(
            fullPath,
            GenericRead | GenericWrite | ReadControl | WriteDac |
            WriteOwner | FileReadAttributes,
            FileShare.Read);
        Inspect(handle, fullPath, expectDirectory: false);
        ApplyExactSecurity(
            handle,
            ProtectedFileSddl,
            fullPath);
        using var stream = new FileStream(handle, FileAccess.ReadWrite);
        stream.Seek(0, SeekOrigin.End);
        using var writer = new StreamWriter(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            leaveOpen: true);
        writer.Write(content);
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    internal static FileStream OpenExclusiveFile(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
        }

        var fullPath = Path.GetFullPath(path);
        var handle = OpenOrCreateFile(
            fullPath,
            GenericRead | GenericWrite | ReadControl | WriteDac |
            WriteOwner | FileReadAttributes,
            FileShare.None);
        try
        {
            Inspect(handle, fullPath, expectDirectory: false);
            ApplyExactSecurity(handle, ProtectedFileSddl, fullPath);
            return new FileStream(handle, FileAccess.ReadWrite);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal static bool DeleteFile(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }

        using var handle = TryOpenExisting(
            Path.GetFullPath(path),
            Delete | FileReadAttributes,
            FileShare.ReadWrite | FileShare.Delete,
            expectDirectory: false);
        if (handle is null) return false;

        // Reparse points and hard links are safe to unlink by handle: Windows
        // removes this directory entry without opening or modifying a target.
        if (!SetFileInformationByHandle(
                handle,
                FileDispositionInfo,
                new FileDispositionInformation { DeleteFile = true },
                Marshal.SizeOf<FileDispositionInformation>()))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Windows could not safely delete {path}.");
        }
        return true;
    }

    internal static bool DeleteEmptyDirectory(
        string path,
        TrustedFileIdentity expectedIdentity)
    {
        if (!OperatingSystem.IsWindows())
        {
            if (!Directory.Exists(path)) return false;
            Directory.Delete(path);
            return true;
        }

        var fullPath = Path.GetFullPath(path);
        using var handle = TryOpenExisting(
            fullPath,
            Delete | FileReadAttributes,
            FileShare.Read,
            expectDirectory: true);
        if (handle is null) return false;

        var actualIdentity = Inspect(
            handle,
            fullPath,
            expectDirectory: true);
        if (actualIdentity != expectedIdentity)
        {
            throw new InvalidDataException(
                $"Refusing to delete a replacement directory at {fullPath}.");
        }
        if (SetFileInformationByHandle(
                handle,
                FileDispositionInfo,
                new FileDispositionInformation { DeleteFile = true },
                Marshal.SizeOf<FileDispositionInformation>()))
        {
            return true;
        }

        var error = Marshal.GetLastWin32Error();
        if (error == ErrorDirectoryNotEmpty) return false;
        throw new Win32Exception(
            error,
            $"Windows could not safely delete the empty directory {fullPath}.");
    }

    private static SafeFileHandle OpenOrCreateFile(
        string path,
        uint desiredAccess,
        FileShare share)
    {
        var handle = CreateFile(
            path,
            desiredAccess,
            share,
            IntPtr.Zero,
            FileMode.OpenOrCreate,
            FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(
                error,
                $"Windows could not safely open {path}.");
        }
        return handle;
    }

    private static SafeFileHandle OpenExisting(
        string path,
        uint desiredAccess,
        FileShare share,
        bool expectDirectory)
    {
        var handle = TryOpenExisting(
            path,
            desiredAccess,
            share,
            expectDirectory);
        return handle ?? throw new FileNotFoundException(
            $"The protected path was not found: {path}",
            path);
    }

    private static SafeFileHandle? TryOpenExisting(
        string path,
        uint desiredAccess,
        FileShare share,
        bool expectDirectory)
    {
        var flags = FileFlagOpenReparsePoint |
                    (expectDirectory ? FileFlagBackupSemantics : 0);
        var handle = CreateFile(
            path,
            desiredAccess,
            share,
            IntPtr.Zero,
            FileMode.Open,
            flags,
            IntPtr.Zero);
        if (!handle.IsInvalid) return handle;

        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        if (error is ErrorFileNotFound or ErrorPathNotFound) return null;
        throw new Win32Exception(
            error,
            $"Windows could not safely open {path}.");
    }

    private static SafeFileHandle? TryOpenExistingEntry(
        string path,
        uint desiredAccess,
        FileShare share)
    {
        var handle = CreateFile(
            path,
            desiredAccess,
            share,
            IntPtr.Zero,
            FileMode.Open,
            FileFlagOpenReparsePoint | FileFlagBackupSemantics,
            IntPtr.Zero);
        if (!handle.IsInvalid) return handle;

        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        if (error is ErrorFileNotFound or ErrorPathNotFound) return null;
        throw new Win32Exception(
            error,
            $"Windows could not safely open the existing entry {path}.");
    }

    private static TrustedFileIdentity Inspect(
        SafeFileHandle handle,
        string path,
        bool expectDirectory)
    {
        if (!GetFileInformationByHandle(handle, out var information))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Windows could not inspect {path}.");
        }

        if ((information.FileAttributes & FileAttributeReparsePoint) != 0)
        {
            throw new InvalidDataException(
                $"Refusing privileged access through reparse point {path}.");
        }
        var isDirectory =
            (information.FileAttributes & FileAttributeDirectory) != 0;
        if (isDirectory != expectDirectory)
        {
            throw new InvalidDataException(
                $"The protected path has an unexpected type: {path}");
        }
        if (!isDirectory && information.NumberOfLinks != 1)
        {
            throw new InvalidDataException(
                $"Refusing privileged access through hard-linked file {path}.");
        }
        return new TrustedFileIdentity(
            information.VolumeSerialNumber,
            information.FileIndexHigh,
            information.FileIndexLow);
    }

    private static void CreateProtectedDirectoryExact(string path)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(
                ProtectedDirectorySddl,
                SecurityDescriptorRevision,
                out var descriptor,
                out _))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Windows could not construct the security policy for {path}.");
        }

        try
        {
            var attributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                SecurityDescriptor = descriptor,
                InheritHandle = false,
            };
            if (!CreateDirectory(path, ref attributes))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    $"Windows could not atomically create {path}.");
            }
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    private static void RenameByHandle(
        SafeFileHandle handle,
        string destination,
        string description)
    {
        var destinationBytes = Encoding.Unicode.GetBytes(
            Path.GetFullPath(destination));
        var fileNameOffset = Marshal.OffsetOf<FileRenameInformation>(
            nameof(FileRenameInformation.FileName)).ToInt32();
        var bufferSize = checked(fileNameOffset + destinationBytes.Length);
        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            Span<byte> zeroes = stackalloc byte[fileNameOffset];
            zeroes.Clear();
            Marshal.Copy(zeroes.ToArray(), 0, buffer, zeroes.Length);
            Marshal.WriteIntPtr(
                buffer,
                Marshal.OffsetOf<FileRenameInformation>(
                    nameof(FileRenameInformation.RootDirectory)).ToInt32(),
                IntPtr.Zero);
            Marshal.WriteInt32(
                buffer,
                Marshal.OffsetOf<FileRenameInformation>(
                    nameof(FileRenameInformation.FileNameLength)).ToInt32(),
                destinationBytes.Length);
            Marshal.Copy(
                destinationBytes,
                0,
                IntPtr.Add(buffer, fileNameOffset),
                destinationBytes.Length);

            if (!SetFileInformationByHandle(
                    handle,
                    FileRenameInfo,
                    buffer,
                    bufferSize))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    $"Windows could not rename the {description}.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool TryDeleteEntryByHandle(SafeFileHandle handle)
    {
        return SetFileInformationByHandle(
            handle,
            FileDispositionInfo,
            new FileDispositionInformation { DeleteFile = true },
            Marshal.SizeOf<FileDispositionInformation>());
    }

    private static void ApplyExactSecurity(
        SafeFileHandle handle,
        string sddl,
        string path)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(
                sddl,
                SecurityDescriptorRevision,
                out var descriptor,
                out _))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Windows could not construct the security policy for {path}.");
        }

        try
        {
            if (!GetSecurityDescriptorOwner(
                    descriptor,
                    out var owner,
                    out _) ||
                !GetSecurityDescriptorDacl(
                    descriptor,
                    out var daclPresent,
                    out var dacl,
                    out _) ||
                !daclPresent)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    $"Windows could not read the security policy for {path}.");
            }

            var result = SetSecurityInfo(
                handle,
                SecurityObjectType.FileObject,
                OwnerSecurityInformation |
                DaclSecurityInformation |
                ProtectedDaclSecurityInformation,
                owner,
                IntPtr.Zero,
                dacl,
                IntPtr.Zero);
            if (result != 0)
            {
                throw new Win32Exception(
                    unchecked((int)result),
                    $"Windows could not apply the security policy to {path}.");
            }
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    private enum SecurityObjectType
    {
        FileObject = 1,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        internal uint LowDateTime;
        internal uint HighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        internal uint FileAttributes;
        internal FileTime CreationTime;
        internal FileTime LastAccessTime;
        internal FileTime LastWriteTime;
        internal uint VolumeSerialNumber;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh;
        internal uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInformation
    {
        [MarshalAs(UnmanagedType.U1)]
        internal bool DeleteFile;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FileRenameInformation
    {
        [MarshalAs(UnmanagedType.Bool)]
        internal bool ReplaceIfExists;
        internal IntPtr RootDirectory;
        internal uint FileNameLength;
        internal char FileName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        internal int Length;
        internal IntPtr SecurityDescriptor;

        [MarshalAs(UnmanagedType.Bool)]
        internal bool InheritHandle;
    }

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        FileMode creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation information);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        int fileInformationClass,
        in FileDispositionInformation fileInformation,
        int bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        int fileInformationClass,
        IntPtr fileInformation,
        int bufferSize);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateDirectoryW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectory(
        string path,
        ref SecurityAttributes securityAttributes);

    [DllImport(
        "advapi32.dll",
        EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(
        string stringSecurityDescriptor,
        int stringSdRevision,
        out IntPtr securityDescriptor,
        out uint securityDescriptorSize);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSecurityDescriptorOwner(
        IntPtr securityDescriptor,
        out IntPtr owner,
        [MarshalAs(UnmanagedType.Bool)] out bool ownerDefaulted);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSecurityDescriptorDacl(
        IntPtr securityDescriptor,
        [MarshalAs(UnmanagedType.Bool)] out bool daclPresent,
        out IntPtr dacl,
        [MarshalAs(UnmanagedType.Bool)] out bool daclDefaulted);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint SetSecurityInfo(
        SafeFileHandle handle,
        SecurityObjectType objectType,
        uint securityInformation,
        IntPtr owner,
        IntPtr group,
        IntPtr dacl,
        IntPtr sacl);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
