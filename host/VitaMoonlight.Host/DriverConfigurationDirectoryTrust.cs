using System.Text.Json;

namespace VitaMoonlight.Host;

internal sealed record DriverConfigurationDirectoryIdentityRecord(
    int FormatVersion,
    uint VolumeSerialNumber,
    uint FileIndexHigh,
    uint FileIndexLow);

internal sealed record DriverConfigurationDirectoryPreparation(
    TrustedDirectoryLease Lease,
    bool Recreated,
    string? RetainedQuarantinePath);

/// <summary>
/// Pins the upstream driver's fixed C:\VirtualDisplayDriver directory to the
/// file identity of a directory created by this host with a protected DACL.
/// Existing objects at that name are never adopted, parsed, or secured in
/// place.
/// </summary>
internal static class DriverConfigurationDirectoryTrust
{
    private const int CurrentFormatVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    internal static string IdentityFile =>
        Path.Combine(
            HostStatePaths.Root,
            "display-driver-directory-identity.json");

    internal static DriverConfigurationDirectoryPreparation
        PrepareForInstallOrRepair(string directoryPath)
    {
        MachineStateSecurity.Secure();

        if (TryReadRecord(out var record, out _) &&
            TryAcquireMatchingLease(
                directoryPath,
                record!,
                out var currentLease,
                out _))
        {
            return new DriverConfigurationDirectoryPreparation(
                currentLease!,
                Recreated: false,
                RetainedQuarantinePath: null);
        }

        var replacement =
            TrustedFileSystem.ReplaceWithNewProtectedDirectory(directoryPath);
        try
        {
            var replacementRecord = CreateRecord(
                replacement.Lease.Identity);
            TrustedFileSystem.WriteAllText(
                IdentityFile,
                JsonSerializer.Serialize(replacementRecord, JsonOptions));

            // Verify the protected record through the same read path used by
            // later operational commands before allowing any driver process.
            if (!TryReadRecord(out var persisted, out var recordError) ||
                !Matches(persisted!, replacement.Lease.Identity))
            {
                throw new InvalidDataException(
                    "The protected virtual-display directory identity could " +
                    $"not be persisted ({recordError}).");
            }

            return new DriverConfigurationDirectoryPreparation(
                replacement.Lease,
                Recreated: true,
                replacement.RetainedQuarantinePath);
        }
        catch
        {
            replacement.Lease.Dispose();
            throw;
        }
    }

    internal static TrustedDirectoryLease AcquireVerified(
        string directoryPath)
    {
        if (!TryReadRecord(out var record, out var recordError))
        {
            throw RepairRequired(
                "Its protected directory identity is missing or invalid" +
                (string.IsNullOrWhiteSpace(recordError)
                    ? "."
                    : $" ({recordError})."));
        }

        if (!TryAcquireMatchingLease(
                directoryPath,
                record!,
                out var lease,
                out var mismatch))
        {
            throw RepairRequired(mismatch);
        }
        return lease!;
    }

    internal static bool TryAcquireVerified(
        string directoryPath,
        out TrustedDirectoryLease? lease,
        out string reason)
    {
        lease = null;
        if (!TryReadRecord(out var record, out var recordError))
        {
            reason =
                "protected directory identity is missing or invalid" +
                (string.IsNullOrWhiteSpace(recordError)
                    ? string.Empty
                    : $" ({recordError})");
            return false;
        }
        return TryAcquireMatchingLease(
            directoryPath,
            record!,
            out lease,
            out reason);
    }

    internal static bool DeleteTrustedDirectoryIfEmpty(
        string directoryPath)
    {
        // Deleting the fixed directory is intentionally implemented as a
        // separate exact-identity operation. A mismatched or missing record is
        // never converted into a pathname-based delete.
        TrustedFileIdentity identity;
        using (var lease = AcquireVerified(directoryPath))
        {
            identity = lease.Identity;
        }

        var deleted = TrustedFileSystem.DeleteEmptyDirectory(
            directoryPath,
            identity);
        if (deleted)
        {
            TrustedFileSystem.DeleteFile(IdentityFile);
        }
        return deleted;
    }

    internal static DriverConfigurationDirectoryIdentityRecord CreateRecord(
        TrustedFileIdentity identity) =>
        new(
            CurrentFormatVersion,
            identity.VolumeSerialNumber,
            identity.FileIndexHigh,
            identity.FileIndexLow);

    internal static bool Matches(
        DriverConfigurationDirectoryIdentityRecord record,
        TrustedFileIdentity identity) =>
        record.FormatVersion == CurrentFormatVersion &&
        record.VolumeSerialNumber == identity.VolumeSerialNumber &&
        record.FileIndexHigh == identity.FileIndexHigh &&
        record.FileIndexLow == identity.FileIndexLow;

    private static bool TryReadRecord(
        out DriverConfigurationDirectoryIdentityRecord? record,
        out string error)
    {
        record = null;
        error = string.Empty;
        try
        {
            if (!File.Exists(IdentityFile))
            {
                error = "record not found";
                return false;
            }
            record =
                JsonSerializer.Deserialize<
                    DriverConfigurationDirectoryIdentityRecord>(
                    TrustedFileSystem.ReadAllText(IdentityFile),
                    JsonOptions);
            if (record is null)
            {
                error = "record is empty";
                return false;
            }
            if (record.FormatVersion != CurrentFormatVersion)
            {
                error = $"unsupported record format {record.FormatVersion}";
                record = null;
                return false;
            }
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                JsonException or
                InvalidDataException or
                System.ComponentModel.Win32Exception)
        {
            error = exception.Message;
            record = null;
            return false;
        }
    }

    private static bool TryAcquireMatchingLease(
        string directoryPath,
        DriverConfigurationDirectoryIdentityRecord record,
        out TrustedDirectoryLease? lease,
        out string reason)
    {
        lease = null;
        try
        {
            var candidate =
                TrustedFileSystem.AcquireDirectoryLease(directoryPath);
            if (!Matches(record, candidate.Identity))
            {
                candidate.Dispose();
                reason =
                    "The fixed directory is not the protected directory " +
                    "created by Vita Moonlight Host.";
                return false;
            }

            lease = candidate;
            reason = string.Empty;
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                InvalidDataException or
                System.ComponentModel.Win32Exception)
        {
            reason = exception.Message;
            return false;
        }
    }

    private static InvalidOperationException RepairRequired(string reason) =>
        new(
            $"{reason} Run Install/update display driver as Administrator. " +
            "The repair action safely detaches an older directory without " +
            "reading it and creates a new protected configuration directory. " +
            "If Windows reports a sharing violation, restart Windows and " +
            "repeat the repair.");
}
