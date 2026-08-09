using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VitaMoonlight.Host;

internal enum AudioEndpointRole
{
    Console = 0,
    Multimedia = 1,
    Communications = 2,
}

internal sealed record AudioEndpointRecoveryRecord(
    string? ConsoleDeviceId,
    string? MultimediaDeviceId,
    string? CommunicationsDeviceId)
{
    internal IEnumerable<(AudioEndpointRole Role, string DeviceId)> Entries()
    {
        if (!string.IsNullOrWhiteSpace(ConsoleDeviceId))
        {
            yield return (AudioEndpointRole.Console, ConsoleDeviceId);
        }
        if (!string.IsNullOrWhiteSpace(MultimediaDeviceId))
        {
            yield return (AudioEndpointRole.Multimedia, MultimediaDeviceId);
        }
        if (!string.IsNullOrWhiteSpace(CommunicationsDeviceId))
        {
            yield return (
                AudioEndpointRole.Communications,
                CommunicationsDeviceId);
        }
    }
}

internal sealed record AudioEndpointRestoreResult(
    bool Succeeded,
    IReadOnlyList<AudioEndpointRole> RestoredRoles,
    IReadOnlyList<AudioEndpointRole> UnavailableRoles,
    string? Detail)
{
    internal static AudioEndpointRestoreResult NotRequired { get; } =
        new(true, [], [], null);
}

internal sealed record PendingAudioEndpointRecoveryRecord(
    int FormatVersion,
    long Revision,
    DateTimeOffset CapturedAt,
    AudioEndpointRecoveryRecord Defaults);

internal sealed record PendingAudioEndpointRecoveryEnvelope(
    int StorageVersion,
    PendingAudioEndpointRecoveryRecord State,
    string PayloadSha256);

internal interface IAudioEndpointBackend : IDisposable
{
    string? GetDefaultRenderDeviceId(AudioEndpointRole role);
    bool IsRenderDeviceActive(string deviceId);
    void SetDefaultRenderDevice(string deviceId, AudioEndpointRole role);
}

/// <summary>
/// Captures and restores only the exact Windows render endpoints that were
/// defaults before the Vita display handoff. DisplayPort/HDMI audio endpoints
/// can disappear while Windows disables the physical monitor; waiting for the
/// same endpoint to re-enumerate and reasserting it prevents applications from
/// remaining attached to a missing output after the desktop is restored.
/// </summary>
internal static class AudioEndpointRecoveryService
{
    internal const string IsolatedWorkerCommand =
        "_audio-endpoint-worker";
    private const string IsolatedCaptureAction = "capture";
    private const string IsolatedRestoreAction = "restore";
    private const int MaximumDeviceIdLength = 2048;
    private const int ProductionRestoreAttempts = 40;
    private const int ProductionRestoreDelayMilliseconds = 250;
    private const int IsolatedCaptureTimeoutMilliseconds = 3000;
    private const int IsolatedRestoreAttemptTimeoutMilliseconds = 2000;
    private const int IsolatedTerminationWaitMilliseconds = 250;
    private const int IsolatedRestoreBudgetMilliseconds = 10000;
    private const int CurrentPendingFormatVersion = 1;
    private const int CurrentPendingStorageVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    internal static AudioEndpointRecoveryRecord? CaptureCurrentDefaults()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var operation = RunIsolatedWorkerProcess(
            IsolatedCaptureAction,
            IsolatedCaptureTimeoutMilliseconds,
            captureOutput: true);
        if (!operation.Started || operation.TimedOut ||
            operation.ExitCode != 0 ||
            string.IsNullOrWhiteSpace(operation.StandardOutput))
        {
            return null;
        }
        try
        {
            var captured = JsonSerializer.Deserialize<
                AudioEndpointRecoveryRecord>(
                operation.StandardOutput,
                JsonOptions);
            return captured is not null &&
                   captured.Entries().Any() &&
                   captured.Entries().All(entry =>
                       IsValidCapturedDeviceId(entry.DeviceId))
                ? captured
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static AudioEndpointRecoveryRecord?
        CaptureCurrentDefaultsInProcess()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using IAudioEndpointBackend backend =
                new WindowsCoreAudioEndpointBackend();
            return Capture(backend);
        }
        catch (Exception error) when (IsOperationalAudioError(error))
        {
            // Audio recovery is supplementary to the fail-safe display
            // transaction. A PC with no active render device must still be
            // able to begin and later recover a Vita display session.
            return null;
        }
    }

    internal static AudioEndpointRecoveryRecord? Capture(
        IAudioEndpointBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        var captured = new AudioEndpointRecoveryRecord(
            CaptureRole(backend, AudioEndpointRole.Console),
            CaptureRole(backend, AudioEndpointRole.Multimedia),
            CaptureRole(backend, AudioEndpointRole.Communications));
        return captured.Entries().Any() ? captured : null;
    }

    private static AudioEndpointRestoreResult RestoreCurrentDefaultsInProcess(
        AudioEndpointRecoveryRecord? captured,
        bool waitForEndpoint = true)
    {
        if (captured is null || !OperatingSystem.IsWindows())
        {
            return AudioEndpointRestoreResult.NotRequired;
        }
        try
        {
            using IAudioEndpointBackend backend =
                new WindowsCoreAudioEndpointBackend();
            return Restore(
                captured,
                backend,
                waitForEndpoint ? ProductionRestoreAttempts : 1,
                () => Thread.Sleep(
                    ProductionRestoreDelayMilliseconds));
        }
        catch (Exception error) when (IsOperationalAudioError(error))
        {
            return new AudioEndpointRestoreResult(
                false,
                [],
                captured.Entries().Select(entry => entry.Role).ToArray(),
                error.Message);
        }
    }

    internal static void SavePending(
        DateTimeOffset capturedAt,
        AudioEndpointRecoveryRecord defaults)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        using var pendingLock = AcquirePendingLock();
        var hasAnyRecord = PendingRecordExists;
        if (!TryLoadPending(out var existing, out var loadError) &&
            hasAnyRecord)
        {
            throw new InvalidDataException(
                "The protected pending audio recovery records are unreadable; the existing exact endpoint obligation was retained rather than overwritten. " +
                loadError);
        }
        var pending = existing is not null
            ? new PendingAudioEndpointRecoveryRecord(
                CurrentPendingFormatVersion,
                checked(existing.Revision + 1),
                existing.CapturedAt <= capturedAt
                    ? existing.CapturedAt
                    : capturedAt,
                MergePreferExisting(existing.Defaults, defaults))
            : new PendingAudioEndpointRecoveryRecord(
                CurrentPendingFormatVersion,
                1,
                capturedAt,
                defaults);
        SavePendingState(pending);
    }

    internal static AudioEndpointRestoreResult RestorePending(
        bool waitForEndpoint = true)
    {
        if (!PendingRecordExists || !OperatingSystem.IsWindows())
        {
            return AudioEndpointRestoreResult.NotRequired;
        }

        var runtime = Stopwatch.StartNew();
        var attempts = 0;
        IsolatedWorkerProcessResult? lastOperation = null;
        do
        {
            var remaining = IsolatedRestoreBudgetMilliseconds -
                checked((int)Math.Min(
                    int.MaxValue,
                    runtime.ElapsedMilliseconds));
            if (remaining <= 0) break;
            attempts++;
            lastOperation = RunIsolatedWorkerProcess(
                IsolatedRestoreAction,
                Math.Min(
                    IsolatedRestoreAttemptTimeoutMilliseconds,
                    remaining),
                captureOutput: false);
            if (!PendingRecordExists ||
                lastOperation is
                {
                    Started: true,
                    TimedOut: false,
                    ExitCode: 0,
                })
            {
                return AudioEndpointRestoreResult.NotRequired;
            }
            if (!waitForEndpoint) break;

            remaining = IsolatedRestoreBudgetMilliseconds -
                checked((int)Math.Min(
                    int.MaxValue,
                    runtime.ElapsedMilliseconds));
            if (remaining <= 0) break;
            Thread.Sleep(Math.Min(
                ProductionRestoreDelayMilliseconds,
                remaining));
        }
        while (waitForEndpoint && PendingRecordExists);

        AudioEndpointRole[] unavailable;
        try
        {
            unavailable = InspectPending()?.Defaults.Entries()
                .Select(entry => entry.Role)
                .Distinct()
                .ToArray() ?? [];
        }
        catch (Exception error) when (
            error is IOException or
                UnauthorizedAccessException or
                InvalidDataException or
                InvalidOperationException or
                System.ComponentModel.Win32Exception or
                System.Security.SecurityException)
        {
            unavailable = [];
        }
        var detail = lastOperation switch
        {
            { TimedOut: true } =>
                "Windows Core Audio did not answer before the isolated attempt timeout; the helper process was terminated safely.",
            { Started: false, Error: not null } =>
                "The isolated Windows audio helper could not start: " +
                lastOperation.Error,
            { ExitCode: not 0 } =>
                "The exact endpoint remained unavailable after the bounded isolated audio attempt.",
            _ =>
                "The pending exact audio endpoint could not be restored.",
        };
        return new AudioEndpointRestoreResult(
            false,
            [],
            unavailable,
            $"{detail} Attempts: {attempts}.");
    }

    private static AudioEndpointRestoreResult RestorePendingInProcess(
        bool waitForEndpoint = false)
    {
        if (!PendingRecordExists)
        {
            return AudioEndpointRestoreResult.NotRequired;
        }
        using var pendingLock = AcquirePendingLock();
        if (!PendingRecordExists)
        {
            return AudioEndpointRestoreResult.NotRequired;
        }
        if (!TryLoadPending(out var pending, out var loadError) ||
            pending is null)
        {
            // Audio state never authorizes display mutation, so retaining a
            // damaged redundant record cannot cause display churn. Keep it
            // for support/recovery rather than silently discarding the last
            // exact endpoint obligation.
            return new AudioEndpointRestoreResult(
                false,
                [],
                [],
                "The protected pending audio recovery records are unreadable and were retained. " +
                loadError);
        }
        var result = RestoreCurrentDefaultsInProcess(
            pending.Defaults,
            waitForEndpoint);
        if (result.Succeeded)
        {
            DeletePendingRecords();
        }
        else if (result.RestoredRoles.Count > 0)
        {
            // Once a role has been restored, stop claiming authority over it.
            // If the user later changes that default legitimately, a still-
            // unplugged communications endpoint must not roll their Console
            // or Multimedia choice back on every retry.
            var remaining = RetainOnlyRoles(
                pending.Defaults,
                result.UnavailableRoles);
            if (remaining.Entries().Any())
            {
                SavePendingState(pending with
                {
                    Revision = checked(pending.Revision + 1),
                    Defaults = remaining,
                });
            }
            else
            {
                DeletePendingRecords();
            }
        }
        return result;
    }

    internal static int RunIsolatedWorker(string[] arguments)
    {
        if (!OperatingSystem.IsWindows() || arguments.Length != 1)
        {
            return 2;
        }
        if (string.Equals(
                arguments[0],
                IsolatedCaptureAction,
                StringComparison.Ordinal))
        {
            var captured = CaptureCurrentDefaultsInProcess();
            Console.Out.Write(JsonSerializer.Serialize(captured, JsonOptions));
            return 0;
        }
        if (string.Equals(
                arguments[0],
                IsolatedRestoreAction,
                StringComparison.Ordinal))
        {
            MachineStateSecurity.RequireAudioRecoveryWorkerAccess();
            return RestorePendingInProcess(waitForEndpoint: false).Succeeded
                ? 0
                : 1;
        }
        return 2;
    }

    internal static PendingAudioEndpointRecoveryRecord? InspectPending()
    {
        if (!PendingRecordExists) return null;
        using var pendingLock = AcquirePendingLock();
        if (!PendingRecordExists) return null;
        if (!TryLoadPending(out var pending, out var loadError) ||
            pending is null)
        {
            throw new InvalidDataException(
                "The protected pending audio recovery records are unreadable. " +
                loadError);
        }
        return pending;
    }

    internal static AudioEndpointRestoreResult Restore(
        AudioEndpointRecoveryRecord? captured,
        IAudioEndpointBackend backend,
        int attempts,
        Action waitBetweenAttempts)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(waitBetweenAttempts);
        if (attempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(attempts));
        }
        if (captured is null)
        {
            return AudioEndpointRestoreResult.NotRequired;
        }

        var entries = captured.Entries()
            .Where(entry => IsValidCapturedDeviceId(entry.DeviceId))
            .ToArray();
        var invalidRoles = captured.Entries()
            .Where(entry => !IsValidCapturedDeviceId(entry.DeviceId))
            .Select(entry => entry.Role)
            .Distinct()
            .ToArray();
        if (entries.Length == 0)
        {
            return invalidRoles.Length == 0
                ? AudioEndpointRestoreResult.NotRequired
                : new AudioEndpointRestoreResult(
                    false,
                    [],
                    invalidRoles,
                    "The saved audio endpoint identifier was invalid.");
        }

        Exception? lastError = null;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            var activeEntries = new List<(
                AudioEndpointRole Role,
                string DeviceId)>();
            foreach (var entry in entries)
            {
                try
                {
                    if (backend.IsRenderDeviceActive(entry.DeviceId))
                    {
                        activeEntries.Add(entry);
                    }
                }
                catch (Exception error) when (IsOperationalAudioError(error))
                {
                    lastError = error;
                }
            }

            foreach (var entry in activeEntries)
            {
                try
                {
                    if (!string.Equals(
                            backend.GetDefaultRenderDeviceId(entry.Role),
                            entry.DeviceId,
                            StringComparison.Ordinal))
                    {
                        backend.SetDefaultRenderDevice(
                            entry.DeviceId,
                            entry.Role);
                    }
                }
                catch (Exception error) when (IsOperationalAudioError(error))
                {
                    lastError = error;
                }
            }

            var restored = new List<AudioEndpointRole>();
            foreach (var entry in entries)
            {
                try
                {
                    if (string.Equals(
                            backend.GetDefaultRenderDeviceId(entry.Role),
                            entry.DeviceId,
                            StringComparison.Ordinal))
                    {
                        restored.Add(entry.Role);
                    }
                }
                catch (Exception error) when (IsOperationalAudioError(error))
                {
                    lastError = error;
                }
            }
            restored = restored.Distinct().ToList();
            var unavailable = entries
                .Select(entry => entry.Role)
                .Concat(invalidRoles)
                .Distinct()
                .Except(restored)
                .ToArray();
            if (unavailable.Length == 0)
            {
                return new AudioEndpointRestoreResult(
                    true,
                    restored,
                    [],
                    null);
            }
            if (attempt < attempts - 1)
            {
                waitBetweenAttempts();
            }
        }

        var finalRestored = entries
            .Where(entry =>
            {
                try
                {
                    return string.Equals(
                        backend.GetDefaultRenderDeviceId(entry.Role),
                        entry.DeviceId,
                        StringComparison.Ordinal);
                }
                catch (Exception error) when (IsOperationalAudioError(error))
                {
                    lastError = error;
                    return false;
                }
            })
            .Select(entry => entry.Role)
            .Distinct()
            .ToArray();
        var finalUnavailable = entries
            .Select(entry => entry.Role)
            .Concat(invalidRoles)
            .Distinct()
            .Except(finalRestored)
            .ToArray();
        return new AudioEndpointRestoreResult(
            finalUnavailable.Length == 0,
            finalRestored,
            finalUnavailable,
            lastError?.Message ??
                "The pre-stream audio endpoint did not become available before the bounded restore deadline.");
    }

    private static string? CaptureRole(
        IAudioEndpointBackend backend,
        AudioEndpointRole role)
    {
        try
        {
            var deviceId = backend.GetDefaultRenderDeviceId(role);
            return IsValidCapturedDeviceId(deviceId) ? deviceId : null;
        }
        catch (Exception error) when (IsOperationalAudioError(error))
        {
            return null;
        }
    }

    private static bool IsValidCapturedDeviceId(string? deviceId) =>
        !string.IsNullOrWhiteSpace(deviceId) &&
        deviceId.Length <= MaximumDeviceIdLength &&
        deviceId.IndexOf('\0') < 0;

    private sealed record PendingReadCandidate(
        bool Exists,
        PendingAudioEndpointRecoveryRecord? State,
        string? Error);

    private sealed record IsolatedWorkerProcessResult(
        bool Started,
        bool TimedOut,
        int ExitCode,
        string? StandardOutput,
        string? Error);

    private static bool PendingRecordExists =>
        File.Exists(HostStatePaths.AudioRecoveryFile) ||
        File.Exists(HostStatePaths.AudioRecoveryBackupFile);

    private static FileStream AcquirePendingLock()
    {
        try
        {
            return TrustedFileSystem.OpenExclusiveFile(
                HostStatePaths.AudioRecoveryLockFile);
        }
        catch (Exception error) when (
            error is IOException or
                UnauthorizedAccessException or
                System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException(
                "Another Windows audio recovery operation is still running.",
                error);
        }
    }

    private static IsolatedWorkerProcessResult RunIsolatedWorkerProcess(
        string action,
        int timeoutMilliseconds,
        bool captureOutput)
    {
        if (timeoutMilliseconds < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeoutMilliseconds));
        }
        try
        {
            var executable = Environment.ProcessPath;
            // Assembly.Location is intentionally unavailable after a
            // single-file publish. Framework-dependent and contract-test
            // hosts copy the companion DLL beside their apphost, while the
            // production single-file apphost relaunches itself directly.
            var assembly = Path.Combine(
                AppContext.BaseDirectory,
                "VitaMoonlight.Host.dll");
            if (string.IsNullOrWhiteSpace(executable))
            {
                return new IsolatedWorkerProcessResult(
                    false,
                    false,
                    -1,
                    null,
                    "The current host executable path is unavailable.");
            }
            var processName = Path.GetFileNameWithoutExtension(executable);
            var isHostAppHost = string.Equals(
                processName,
                "VitaMoonlight.Host",
                StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(assembly);
            var workerExecutable = executable;
            if (!isHostAppHost &&
                !string.Equals(
                    processName,
                    "dotnet",
                    StringComparison.OrdinalIgnoreCase))
            {
                // Unit/contract test apphosts execute this library too. Do
                // not recursively launch the test executable; run the actual
                // host assembly through the current .NET host instead.
                workerExecutable =
                    Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ??
                    "dotnet";
            }
            var startInfo = new ProcessStartInfo
            {
                FileName = workerExecutable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = captureOutput,
                RedirectStandardError = false,
            };
            if (!isHostAppHost)
            {
                if (string.IsNullOrWhiteSpace(assembly))
                {
                    return new IsolatedWorkerProcessResult(
                        false,
                        false,
                        -1,
                        null,
                        "The managed host assembly path is unavailable.");
                }
                startInfo.ArgumentList.Add(assembly);
            }
            startInfo.ArgumentList.Add(IsolatedWorkerCommand);
            startInfo.ArgumentList.Add(action);

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                return new IsolatedWorkerProcessResult(
                    false,
                    false,
                    -1,
                    null,
                    "Windows refused to start the helper process.");
            }
            Task<string>? output = captureOutput
                ? process.StandardOutput.ReadToEndAsync()
                : null;
            if (!process.WaitForExit(timeoutMilliseconds))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(
                        IsolatedTerminationWaitMilliseconds);
                }
                catch (Exception terminationError) when (
                    terminationError is InvalidOperationException or
                        System.ComponentModel.Win32Exception or
                        NotSupportedException)
                {
                    // The timeout result remains authoritative. The process
                    // may already have exited between WaitForExit and Kill.
                }
                return new IsolatedWorkerProcessResult(
                    true,
                    true,
                    -1,
                    null,
                    null);
            }
            var standardOutput = output is null
                ? null
                : output.GetAwaiter().GetResult();
            return new IsolatedWorkerProcessResult(
                true,
                false,
                process.ExitCode,
                standardOutput,
                null);
        }
        catch (Exception error) when (
            error is IOException or
                UnauthorizedAccessException or
                InvalidOperationException or
                ArgumentException or
                NotSupportedException or
                System.ComponentModel.Win32Exception or
                System.Security.SecurityException)
        {
            return new IsolatedWorkerProcessResult(
                false,
                false,
                -1,
                null,
                error.Message);
        }
    }

    private static bool TryLoadPending(
        out PendingAudioEndpointRecoveryRecord? pending,
        out string? error)
    {
        pending = null;
        error = null;
        var primary = TryReadPending(HostStatePaths.AudioRecoveryFile);
        var backup = TryReadPending(HostStatePaths.AudioRecoveryBackupFile);
        return TrySelectPending(primary, backup, out pending, out error);
    }

    private static bool TrySelectPending(
        PendingReadCandidate primary,
        PendingReadCandidate backup,
        out PendingAudioEndpointRecoveryRecord? pending,
        out string? error)
    {
        pending = null;
        error = null;
        if (!primary.Exists && !backup.Exists) return false;
        var candidates = new[] { primary, backup }
            .Where(candidate => candidate.State is not null)
            .Select(candidate => candidate.State!)
            .OrderByDescending(state => state.Revision)
            .ToArray();
        if (candidates.Length == 0)
        {
            error = JoinErrors(primary.Error, backup.Error);
            return false;
        }
        if (primary.State is { } first &&
            backup.State is { } second &&
            first.Revision == second.Revision &&
            !string.Equals(
                SerializePendingState(first),
                SerializePendingState(second),
                StringComparison.Ordinal))
        {
            error = "The primary and backup records have the same revision but conflicting contents.";
            return false;
        }

        pending = candidates[0];
        return true;
    }

    internal static PendingAudioEndpointRecoveryRecord
        SelectPendingForContractTest(
            PendingAudioEndpointRecoveryRecord? primary,
            PendingAudioEndpointRecoveryRecord? backup)
    {
        if (!TrySelectPending(
                new PendingReadCandidate(true, primary, "primary invalid"),
                new PendingReadCandidate(true, backup, "backup invalid"),
                out var selected,
                out var error) ||
            selected is null)
        {
            throw new InvalidDataException(error);
        }
        return selected;
    }

    private static PendingReadCandidate TryReadPending(string path)
    {
        if (!File.Exists(path))
        {
            return new PendingReadCandidate(false, null, null);
        }
        try
        {
            var envelope = JsonSerializer.Deserialize<
                PendingAudioEndpointRecoveryEnvelope>(
                TrustedFileSystem.ReadAllText(path),
                JsonOptions)
                ?? throw new InvalidDataException(
                    "The record is empty.");
            if (envelope.StorageVersion != CurrentPendingStorageVersion ||
                envelope.State is null)
            {
                throw new InvalidDataException(
                    "The record format is incompatible.");
            }
            ValidatePendingState(envelope.State);
            var payload = SerializePendingState(envelope.State);
            var expectedHash = ComputeSha256(payload);
            if (!string.Equals(
                    envelope.PayloadSha256,
                    expectedHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "The record checksum is invalid.");
            }
            return new PendingReadCandidate(true, envelope.State, null);
        }
        catch (Exception readError) when (
            readError is IOException or
                UnauthorizedAccessException or
                InvalidDataException or
                System.ComponentModel.Win32Exception or
                JsonException or
                CryptographicException)
        {
            return new PendingReadCandidate(
                true,
                null,
                $"{Path.GetFileName(path)}: {readError.Message}");
        }
    }

    private static void SavePendingState(
        PendingAudioEndpointRecoveryRecord pending)
    {
        ValidatePendingState(pending);
        var payload = SerializePendingState(pending);
        var envelope = new PendingAudioEndpointRecoveryEnvelope(
            CurrentPendingStorageVersion,
            pending,
            ComputeSha256(payload));
        var json = JsonSerializer.Serialize(envelope, JsonOptions);

        // The newest valid redundant copy wins. If power is lost while one
        // protected file is being truncated/flushed, the other still contains
        // either the previous complete obligation or this newer revision.
        TrustedFileSystem.WriteAllText(
            HostStatePaths.AudioRecoveryBackupFile,
            json);
        TrustedFileSystem.WriteAllText(
            HostStatePaths.AudioRecoveryFile,
            json);
    }

    private static void DeletePendingRecords()
    {
        TrustedFileSystem.DeleteFile(HostStatePaths.AudioRecoveryFile);
        TrustedFileSystem.DeleteFile(
            HostStatePaths.AudioRecoveryBackupFile);
    }

    private static void ValidatePendingState(
        PendingAudioEndpointRecoveryRecord pending)
    {
        var entries = pending.Defaults?.Entries().ToArray() ?? [];
        if (pending.FormatVersion != CurrentPendingFormatVersion ||
            pending.Revision <= 0 ||
            entries.Length == 0 ||
            entries.Any(entry =>
                !IsValidCapturedDeviceId(entry.DeviceId)))
        {
            throw new InvalidDataException(
                "The pending audio recovery state is invalid.");
        }
    }

    private static string SerializePendingState(
        PendingAudioEndpointRecoveryRecord pending) =>
        JsonSerializer.Serialize(pending, JsonOptions);

    private static string ComputeSha256(string payload) =>
        Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(payload)));

    private static string JoinErrors(params string?[] errors)
    {
        var present = errors
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .ToArray();
        return present.Length == 0
            ? "No valid record was available."
            : string.Join(" ", present!);
    }

    internal static AudioEndpointRecoveryRecord MergePreferExisting(
        AudioEndpointRecoveryRecord existing,
        AudioEndpointRecoveryRecord incoming) =>
        new(
            existing.ConsoleDeviceId ?? incoming.ConsoleDeviceId,
            existing.MultimediaDeviceId ?? incoming.MultimediaDeviceId,
            existing.CommunicationsDeviceId ??
                incoming.CommunicationsDeviceId);

    internal static AudioEndpointRecoveryRecord RetainOnlyRoles(
        AudioEndpointRecoveryRecord existing,
        IEnumerable<AudioEndpointRole> roles)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(roles);
        var retained = roles.ToHashSet();
        return new AudioEndpointRecoveryRecord(
            retained.Contains(AudioEndpointRole.Console)
                ? existing.ConsoleDeviceId
                : null,
            retained.Contains(AudioEndpointRole.Multimedia)
                ? existing.MultimediaDeviceId
                : null,
            retained.Contains(AudioEndpointRole.Communications)
                ? existing.CommunicationsDeviceId
                : null);
    }

    private static bool IsOperationalAudioError(Exception error) =>
        error is COMException or
            ExternalException or
            IOException or
            ArgumentException or
            InvalidCastException or
            InvalidOperationException or
            PlatformNotSupportedException or
            UnauthorizedAccessException;
}

internal sealed class WindowsCoreAudioEndpointBackend
    : IAudioEndpointBackend
{
    private const int DeviceStateActive = 0x00000001;
    private readonly IMMDeviceEnumerator enumerator;
    private bool disposed;

    internal WindowsCoreAudioEndpointBackend()
    {
        enumerator = (IMMDeviceEnumerator)(object)
            new MMDeviceEnumeratorComObject();
    }

    public string? GetDefaultRenderDeviceId(AudioEndpointRole role)
    {
        ThrowIfDisposed();
        var result = enumerator.GetDefaultAudioEndpoint(
            AudioDataFlow.Render,
            role,
            out var device);
        if (result < 0)
        {
            // E_NOTFOUND means Windows currently has no default render
            // endpoint for that role. It is ordinary during monitor hotplug.
            if (result == unchecked((int)0x80070490)) return null;
            Marshal.ThrowExceptionForHR(result);
        }
        return GetDeviceIdAndRelease(device);
    }

    public bool IsRenderDeviceActive(string deviceId)
    {
        ThrowIfDisposed();
        var result = enumerator.GetDevice(deviceId, out var device);
        if (result < 0)
        {
            if (result == unchecked((int)0x80070490)) return false;
            Marshal.ThrowExceptionForHR(result);
        }
        try
        {
            result = device.GetState(out var state);
            if (result < 0) Marshal.ThrowExceptionForHR(result);
            return (state & DeviceStateActive) != 0;
        }
        finally
        {
            Marshal.FinalReleaseComObject(device);
        }
    }

    public void SetDefaultRenderDevice(
        string deviceId,
        AudioEndpointRole role)
    {
        ThrowIfDisposed();
        object policyClient = new PolicyConfigClientComObject();
        try
        {
            var result = policyClient switch
            {
                IPolicyConfig policy =>
                    policy.SetDefaultEndpoint(deviceId, role),
                _ => throw new InvalidOperationException(
                    "Windows did not expose a compatible audio policy interface."),
            };
            if (result < 0) Marshal.ThrowExceptionForHR(result);
        }
        finally
        {
            Marshal.FinalReleaseComObject(policyClient);
        }
    }

    internal static bool IsDefaultEndpointPolicyAvailable()
    {
        if (!OperatingSystem.IsWindows()) return false;
        object policyClient = new PolicyConfigClientComObject();
        try
        {
            return policyClient is IPolicyConfig;
        }
        finally
        {
            Marshal.FinalReleaseComObject(policyClient);
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Marshal.FinalReleaseComObject(enumerator);
    }

    private static string? GetDeviceIdAndRelease(IMMDevice device)
    {
        try
        {
            var result = device.GetId(out var idPointer);
            if (result < 0) Marshal.ThrowExceptionForHR(result);
            try
            {
                return Marshal.PtrToStringUni(idPointer);
            }
            finally
            {
                Marshal.FreeCoTaskMem(idPointer);
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(device);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private enum AudioDataFlow
    {
        Render = 0,
        Capture = 1,
        All = 2,
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private sealed class MMDeviceEnumeratorComObject;

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(
            AudioDataFlow dataFlow,
            int stateMask,
            out IntPtr devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(
            AudioDataFlow dataFlow,
            AudioEndpointRole role,
            out IMMDevice device);

        [PreserveSig]
        int GetDevice(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceId,
            out IMMDevice device);

        [PreserveSig]
        int RegisterEndpointNotificationCallback(IntPtr client);

        [PreserveSig]
        int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(
            ref Guid interfaceId,
            int classContext,
            IntPtr activationParameters,
            out IntPtr interfacePointer);

        [PreserveSig]
        int OpenPropertyStore(int access, out IntPtr propertyStore);

        [PreserveSig]
        int GetId(out IntPtr deviceId);

        [PreserveSig]
        int GetState(out int state);
    }

    [ComImport]
    [Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    private sealed class PolicyConfigClientComObject;

    [ComImport]
    [Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(string deviceId, IntPtr format);
        [PreserveSig] int GetDeviceFormat(string deviceId, int isDefault, IntPtr format);
        [PreserveSig] int ResetDeviceFormat(string deviceId);
        [PreserveSig] int SetDeviceFormat(string deviceId, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod(string deviceId, int isDefault, IntPtr defaultPeriod, IntPtr minimumPeriod);
        [PreserveSig] int SetProcessingPeriod(string deviceId, IntPtr period);
        [PreserveSig] int GetShareMode(string deviceId, IntPtr mode);
        [PreserveSig] int SetShareMode(string deviceId, IntPtr mode);
        [PreserveSig] int GetPropertyValue(string deviceId, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue(string deviceId, IntPtr key, IntPtr value);
        [PreserveSig]
        int SetDefaultEndpoint(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceId,
            AudioEndpointRole role);
        [PreserveSig] int SetEndpointVisibility(string deviceId, int visible);
    }

}
