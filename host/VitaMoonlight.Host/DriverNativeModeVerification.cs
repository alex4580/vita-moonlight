using System.Security.Cryptography;
using System.Text.Json;

namespace VitaMoonlight.Host;

internal sealed record DriverNativeModeVerificationRecord(
    int FormatVersion,
    DateTimeOffset VerifiedAt,
    int Width,
    int Height,
    int Fps,
    string ConfigurationSha256);

internal static class DriverNativeModeVerification
{
    private const int CurrentFormatVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    internal static string VerificationFile =>
        Path.Combine(HostStatePaths.Root, "display-driver-verification.json");

    internal static void Invalidate()
    {
        TrustedFileSystem.DeleteFile(VerificationFile);
    }

    internal static void RecordCurrentLocked(
        DisplayTransactionLease transaction)
    {
        transaction.RequireActive();
        var configurationSha256 = ReadCurrentConfigurationSha256();
        var record = CreateRecord(configurationSha256, DateTimeOffset.UtcNow);
        MachineStateSecurity.SecureWhileDisplayTransactionHeld(transaction);
        TrustedFileSystem.WriteAllText(
            VerificationFile,
            JsonSerializer.Serialize(record, JsonOptions));
    }

    internal static bool IsCurrent(out string message)
    {
        return TryGetCurrent(out _, out message);
    }

    internal static bool TryGetCurrent(
        out DriverNativeModeVerificationRecord? current,
        out string message)
    {
        current = null;
        if (!File.Exists(VerificationFile))
        {
            message = "native 960x544 mode has not been verified";
            return false;
        }

        try
        {
            var record = JsonSerializer.Deserialize<DriverNativeModeVerificationRecord>(
                TrustedFileSystem.ReadAllText(VerificationFile),
                JsonOptions);
            if (record is null)
            {
                message = "native-mode verification record is empty";
                return false;
            }

            var configurationSha256 = ReadCurrentConfigurationSha256();
            if (!Matches(record, configurationSha256))
            {
                message = "native-mode verification does not match the current display-driver configuration";
                return false;
            }

            current = record;
            message = $"native 960x544 mode verified {record.VerifiedAt.LocalDateTime:g}";
            return true;
        }
        catch (Exception error) when (
            error is IOException or
                UnauthorizedAccessException or
                JsonException or
                InvalidDataException or
                InvalidOperationException or
                System.ComponentModel.Win32Exception)
        {
            message = $"native-mode verification could not be read ({error.Message})";
            return false;
        }
    }

    internal static DriverNativeModeVerificationRecord CreateRecord(
        string configurationSha256,
        DateTimeOffset verifiedAt)
    {
        var mode = VitaDisplayModes.Native;
        return new DriverNativeModeVerificationRecord(
            CurrentFormatVersion,
            verifiedAt,
            mode.Width,
            mode.Height,
            mode.Fps,
            configurationSha256);
    }

    internal static bool Matches(
        DriverNativeModeVerificationRecord record,
        string configurationSha256)
    {
        var mode = VitaDisplayModes.Native;
        return record.FormatVersion == CurrentFormatVersion &&
               record.Width == mode.Width &&
               record.Height == mode.Height &&
               record.Fps == mode.Fps &&
               FixedTimeHexEquals(record.ConfigurationSha256, configurationSha256);
    }

    internal static string ComputeConfigurationSha256(ReadOnlySpan<byte> configuration) =>
        Convert.ToHexString(SHA256.HashData(configuration));

    private static string ReadCurrentConfigurationSha256()
    {
        using var directoryLease =
            DriverConfigurationDirectoryTrust.AcquireVerified(
                DisplayWizardAdapter.DriverConfigurationDirectoryPath);
        var configurationPath = DisplayWizardAdapter.DriverConfigurationPath;
        if (!File.Exists(configurationPath))
        {
            throw new InvalidDataException(
                $"The display-driver configuration is missing at {configurationPath}.");
        }
        return ComputeConfigurationSha256(
            TrustedFileSystem.ReadAllBytes(configurationPath));
    }

    private static bool FixedTimeHexEquals(string first, string second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second))
        {
            return false;
        }
        try
        {
            var firstBytes = Convert.FromHexString(first);
            var secondBytes = Convert.FromHexString(second);
            return firstBytes.Length == secondBytes.Length &&
                   CryptographicOperations.FixedTimeEquals(firstBytes, secondBytes);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
