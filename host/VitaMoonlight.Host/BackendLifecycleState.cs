using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VitaMoonlight.Host;

internal enum BackendDesiredState
{
    Enabled,
    Disabled,
}

internal enum BackendLifecycleStatus
{
    Enabled,
    Disabled,
    Partial,
    Error,
}

internal enum SunshineBackendKind
{
    None,
    Service,
    Process,
}

internal sealed record SunshineBackendSnapshot(
    SunshineBackendKind Kind,
    string? ServiceName,
    uint? ServiceStartType,
    bool? DelayedAutoStart,
    string? ExecutablePath,
    bool WasRunning);

internal sealed record BackendPersistedState(
    int FormatVersion,
    long Revision,
    BackendDesiredState DesiredState,
    DateTimeOffset UpdatedAtUtc,
    bool RestoreRecoveryTask,
    bool RestoreRescueAgent,
    IReadOnlyList<string> ManagedVirtualDisplayInstancesToRestore,
    SunshineBackendSnapshot Sunshine,
    BackendLifecycleStatus LastKnownStatus,
    string? LastError);

internal sealed record BackendStateEnvelope(
    int StorageVersion,
    BackendPersistedState State,
    string StateSha256);

internal sealed record BackendStateLoadResult(
    BackendPersistedState? State,
    bool Exists,
    bool DisabledIntentMarker,
    bool RecoveredFromRedundantCopy,
    string? StorageWarning);

internal enum UninstallTransactionStage
{
    None,
    InProgress,
    Finalized,
    Invalid,
}

internal sealed class BackendOperationLease : IDisposable
{
    private FileStream? stream;

    internal BackendOperationLease(FileStream stream)
    {
        this.stream = stream;
    }

    internal void RequireActive()
    {
        if (stream is null)
        {
            throw new ObjectDisposedException(
                nameof(BackendOperationLease));
        }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref stream, null)?.Dispose();
    }
}

internal static class BackendLifecycleStateStore
{
    private const string UninstallInProgressValue =
        "vita-moonlight-uninstall-in-progress-v1";
    private const string UninstallFinalizedValue =
        "vita-moonlight-uninstall-finalized-v1";
    private const int CurrentFormatVersion = 1;
    private const int CurrentStorageVersion = 1;
    private const int MaximumStateBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    internal static string StateFile => Path.Combine(
        HostStatePaths.Root,
        "backend-lifecycle.json");

    internal static string BackupFile => Path.Combine(
        HostStatePaths.Root,
        "backend-lifecycle.backup.json");

    internal static string LockFile => Path.Combine(
        HostStatePaths.Root,
        "backend-lifecycle.lock");

    internal static string DisabledIntentFile => Path.Combine(
        HostStatePaths.Root,
        "backend-disabled.intent");

    internal static string UninstallIntentFile => Path.Combine(
        HostStatePaths.Root,
        "uninstall-in-progress.intent");

    /// <returns>
    /// True only when this invocation created the transaction marker. A false
    /// result means an earlier interrupted uninstall already owns the durable
    /// intent and a failed retry must not clear it.
    /// </returns>
    internal static bool BeginUninstall()
    {
        using var operationLock = AcquireLock();
        return BeginUninstallLocked(operationLock);
    }

    internal static bool BeginUninstallLocked(
        BackendOperationLease operationLock)
    {
        operationLock.RequireActive();
        if (File.Exists(UninstallIntentFile))
        {
            try
            {
                var stage = ReadUninstallStage();
                if (stage is UninstallTransactionStage.InProgress or
                    UninstallTransactionStage.Finalized)
                {
                    return false;
                }
            }
            catch (Exception error) when (
                error is IOException or
                    UnauthorizedAccessException or
                    InvalidDataException or
                    System.ComponentModel.Win32Exception)
            {
                // File existence in the protected state directory remains an
                // authoritative uninstall fence. A torn/partial marker must
                // be repairable by the next uninstall attempt, not become a
                // permanent block on the supported retry path.
            }

            TrustedFileSystem.WriteAllText(
                UninstallIntentFile,
                UninstallInProgressValue);
            return false;
        }
        TrustedFileSystem.WriteAllText(
            UninstallIntentFile,
            UninstallInProgressValue);
        return true;
    }

    internal static void MarkUninstallFinalized()
    {
        using var operationLock = AcquireLock();
        var stage = ReadUninstallStage();
        if (stage == UninstallTransactionStage.Finalized) return;
        if (stage != UninstallTransactionStage.InProgress)
        {
            throw new InvalidOperationException(
                "The protected uninstall transaction cannot be finalized because its in-progress stage is unavailable.");
        }
        TrustedFileSystem.WriteAllText(
            UninstallIntentFile,
            UninstallFinalizedValue);
    }

    internal static void CancelUninstall()
    {
        using var operationLock = AcquireLock();
        if (ReadUninstallStage() == UninstallTransactionStage.Finalized)
        {
            throw new InvalidOperationException(
                "A finalized uninstall transaction cannot be rolled back.");
        }
        TrustedFileSystem.DeleteFile(UninstallIntentFile);
    }

    internal static void RequireNoUninstallInProgress()
    {
        if (!IsUninstallInProgress()) return;
        throw new InvalidOperationException(
            "Vita Moonlight Host uninstall is in progress. Finish or retry uninstall before changing host features.");
    }

    internal static bool IsUninstallInProgress()
    {
        return ReadUninstallStage() switch
        {
            UninstallTransactionStage.None => false,
            UninstallTransactionStage.InProgress or
                UninstallTransactionStage.Finalized => true,
            _ => throw new InvalidDataException(
                "The protected uninstall transaction marker is invalid."),
        };
    }

    internal static bool IsUninstallFinalized() =>
        ReadUninstallStage() == UninstallTransactionStage.Finalized;

    internal static UninstallTransactionStage
        ClassifyUninstallMarkerForTest(string? marker) =>
        ClassifyUninstallMarker(marker);

    private static UninstallTransactionStage ReadUninstallStage()
    {
        if (!File.Exists(UninstallIntentFile))
        {
            return UninstallTransactionStage.None;
        }
        var stage = ClassifyUninstallMarker(
            TrustedFileSystem.ReadAllText(UninstallIntentFile));
        if (stage == UninstallTransactionStage.Invalid)
        {
            throw new InvalidDataException(
                "The protected uninstall transaction marker is invalid.");
        }
        return stage;
    }

    private static UninstallTransactionStage ClassifyUninstallMarker(
        string? marker) => marker switch
        {
            null => UninstallTransactionStage.None,
            UninstallInProgressValue => UninstallTransactionStage.InProgress,
            UninstallFinalizedValue => UninstallTransactionStage.Finalized,
            _ => UninstallTransactionStage.Invalid,
        };

    internal static BackendStateLoadResult Load()
    {
        var disabledIntent = ReadDisabledIntentMarker();
        var primary = TryRead(StateFile);
        var backup = TryRead(BackupFile);

        if (!primary.Exists && !backup.Exists)
        {
            if (disabledIntent)
            {
                throw new InvalidDataException(
                    "A durable Disabled intent exists, but both lifecycle snapshots are missing. " +
                    "Vita Moonlight remains paused because the exact saved task and managed-display state cannot be reconstructed safely.");
            }
            return new BackendStateLoadResult(
                null,
                false,
                false,
                false,
                null);
        }

        var candidates = new[] { primary, backup }
            .Where(candidate => candidate.State is not null)
            .OrderByDescending(candidate => candidate.State!.Revision)
            .ToArray();
        if (candidates.Length == 0)
        {
            throw new InvalidDataException(
                "Both protected Vita host-feature state records are unreadable or corrupt. " +
                "Vita Moonlight remains paused so the saved task and managed-display state is not guessed. " +
                $"Repair or restore {StateFile} and {BackupFile}, then run `backend enable` again. " +
                JoinErrors(primary.Error, backup.Error));
        }

        if (primary.State is not null &&
            backup.State is not null &&
            primary.State.Revision == backup.State.Revision &&
            !string.Equals(
                SerializeState(primary.State),
                SerializeState(backup.State),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The protected Vita host-feature state records have the same revision but conflicting contents. " +
                "Vita Moonlight remains paused rather than guessing the saved task and managed-display state.");
        }

        var selected = candidates[0].State!;
        var redundantCopyFailed =
            primary.State is null ||
            backup.State is null ||
            primary.State.Revision != backup.State.Revision;
        var warning = redundantCopyFailed
            ? "One protected Vita host-feature state record was unavailable; the redundant valid copy was used. " +
              "Run `backend enable` or `backend disable` again to repair both records."
            : null;
        return new BackendStateLoadResult(
            selected,
            true,
            disabledIntent,
            redundantCopyFailed,
            warning);
    }

    internal static BackendStateLoadResult LoadForLifecycleAction(
        BackendOperationLease operation)
    {
        operation.RequireActive();
        try
        {
            return Load();
        }
        catch (InvalidDataException)
        {
            var primary = TryRead(StateFile);
            var backup = TryRead(BackupFile);
            var valid = new[] { primary, backup }
                .Where(candidate => candidate.State is not null)
                .OrderByDescending(candidate => candidate.State!.Revision)
                .Select(candidate => candidate.State!)
                .ToArray();
            var markerExists = File.Exists(DisabledIntentFile);

            if (!markerExists && valid.Length == 0)
            {
                // A crash while writing the very first pre-disable backup
                // cannot have changed Windows: Disabled intent is published
                // only after that backup is fully flushed. An explicit
                // lifecycle action may discard the torn pre-intent copies and
                // recapture the current component state.
                TrustedFileSystem.DeleteFile(StateFile);
                TrustedFileSystem.DeleteFile(BackupFile);
                return new BackendStateLoadResult(
                    null,
                    false,
                    false,
                    false,
                    "Recovered an interrupted first lifecycle-state write before Windows components were changed.");
            }

            if (markerExists &&
                valid.FirstOrDefault() is { DesiredState: BackendDesiredState.Disabled })
            {
                // The protected backup is written and flushed before the
                // marker. Therefore a torn marker with a valid Disabled
                // snapshot is unambiguous and can be repaired in place by an
                // explicit Pause/Enable action.
                TrustedFileSystem.WriteAllText(
                    DisabledIntentFile,
                    "vita-moonlight-backend-disabled-v1");
                return Load();
            }

            throw;
        }
    }

    internal static void Save(BackendPersistedState state)
    {
        MachineStateSecurity.Secure();
        SaveAfterStateWasSecured(state);
    }

    internal static void SaveLocked(
        DisplayTransactionLease transaction,
        BackendPersistedState state)
    {
        transaction.RequireActive();
        SaveAfterStateWasSecured(state);
    }

    private static void SaveAfterStateWasSecured(
        BackendPersistedState state)
    {
        ValidateState(state);
        var payload = SerializeState(state);
        var envelope = new BackendStateEnvelope(
            CurrentStorageVersion,
            state,
            ComputeSha256(payload));
        var json = JsonSerializer.Serialize(envelope, JsonOptions);

        if (state.DesiredState == BackendDesiredState.Disabled)
        {
            // The first complete snapshot must precede the marker. A crash
            // while creating a first-ever lifecycle record then leaves no
            // Disabled intent and cannot strand an unchanged installation.
            TrustedFileSystem.WriteAllText(BackupFile, json);
            TrustedFileSystem.WriteAllText(
                DisabledIntentFile,
                "vita-moonlight-backend-disabled-v1");
            TrustedFileSystem.WriteAllText(StateFile, json);
            return;
        }

        // Both copies receive the same monotonically increasing revision. If
        // power is lost between writes, Load chooses the newest valid copy.
        TrustedFileSystem.WriteAllText(BackupFile, json);
        TrustedFileSystem.WriteAllText(StateFile, json);
        // Enable intent becomes visible only after both exact restore
        // snapshots are durable.
        TrustedFileSystem.DeleteFile(DisabledIntentFile);
    }

    internal static BackendOperationLease AcquireLock()
    {
        MachineStateSecurity.Secure();
        try
        {
            return new BackendOperationLease(
                TrustedFileSystem.OpenExclusiveFile(LockFile));
        }
        catch (IOException error)
        {
            throw new InvalidOperationException(
                "Another backend enable, disable, or uninstall operation is already running.",
                error);
        }
    }

    internal static BackendPersistedState WithNextRevision(
        BackendPersistedState state,
        BackendDesiredState desiredState,
        BackendLifecycleStatus status,
        string? error = null) =>
        state with
        {
            FormatVersion = CurrentFormatVersion,
            Revision = checked(state.Revision + 1),
            DesiredState = desiredState,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            LastKnownStatus = status,
            LastError = NormalizeError(error),
        };

    internal static BackendPersistedState Create(
        BackendDesiredState desiredState,
        bool restoreRecoveryTask,
        bool restoreRescueAgent,
        IReadOnlyList<string> managedVirtualDisplayInstancesToRestore,
        SunshineBackendSnapshot sunshine) =>
        new(
            CurrentFormatVersion,
            1,
            desiredState,
            DateTimeOffset.UtcNow,
            restoreRecoveryTask,
            restoreRescueAgent,
            managedVirtualDisplayInstancesToRestore
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(instanceId => instanceId, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            sunshine,
            BackendLifecycleStatus.Partial,
            null);

    internal static string SerializeStateForTest(BackendPersistedState state) =>
        SerializeState(state);

    internal static string ComputeSha256ForTest(string payload) =>
        ComputeSha256(payload);

    private static StateCopy TryRead(string path)
    {
        if (!File.Exists(path)) return new StateCopy(false, null, null);
        try
        {
            var info = new FileInfo(path);
            if (info.Length <= 0 || info.Length > MaximumStateBytes)
            {
                throw new InvalidDataException(
                    $"The lifecycle record has an invalid size ({info.Length} bytes).");
            }
            var json = TrustedFileSystem.ReadAllText(path);
            var envelope = JsonSerializer.Deserialize<BackendStateEnvelope>(
                json,
                JsonOptions)
                ?? throw new InvalidDataException("The lifecycle record is empty.");
            if (envelope.State is null ||
                string.IsNullOrWhiteSpace(envelope.StateSha256))
            {
                throw new InvalidDataException(
                    "The lifecycle record is missing its state or integrity checksum.");
            }
            if (envelope.StorageVersion != CurrentStorageVersion)
            {
                throw new InvalidDataException(
                    $"Unsupported lifecycle storage version {envelope.StorageVersion}.");
            }
            ValidateState(envelope.State);
            var expected = ComputeSha256(SerializeState(envelope.State));
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(expected),
                    ParseSha256(envelope.StateSha256)))
            {
                throw new InvalidDataException(
                    "The lifecycle record integrity check failed.");
            }
            return new StateCopy(true, envelope.State, null);
        }
        catch (Exception error) when (
            error is IOException or
                UnauthorizedAccessException or
                InvalidDataException or
                System.ComponentModel.Win32Exception or
                JsonException or
                FormatException or
                CryptographicException)
        {
            return new StateCopy(true, null, $"{Path.GetFileName(path)}: {error.Message}");
        }
    }

    private static bool ReadDisabledIntentMarker()
    {
        if (!File.Exists(DisabledIntentFile)) return false;
        try
        {
            var marker = TrustedFileSystem.ReadAllText(
                DisabledIntentFile);
            if (!string.Equals(
                    marker,
                    "vita-moonlight-backend-disabled-v1",
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The backend Disabled intent marker has invalid contents.");
            }
            return true;
        }
        catch (Exception error) when (
            error is IOException or
                UnauthorizedAccessException or
                InvalidDataException or
                System.ComponentModel.Win32Exception)
        {
            throw new InvalidDataException(
                "The backend Disabled intent marker is unreadable. " +
                "Vita Moonlight remains paused.",
                error);
        }
    }

    private static void ValidateState(BackendPersistedState state)
    {
        if (state.FormatVersion != CurrentFormatVersion)
        {
            throw new InvalidDataException(
                $"Unsupported Vita host-feature state format version {state.FormatVersion}.");
        }
        if (state.Revision <= 0)
        {
            throw new InvalidDataException("The Vita host-feature state revision is invalid.");
        }
        if (!Enum.IsDefined(state.DesiredState) ||
            !Enum.IsDefined(state.LastKnownStatus) ||
            state.ManagedVirtualDisplayInstancesToRestore is null ||
            state.ManagedVirtualDisplayInstancesToRestore.Any(instanceId =>
                !IsValidDeviceInstanceId(instanceId)) ||
            state.ManagedVirtualDisplayInstancesToRestore.Count !=
                state.ManagedVirtualDisplayInstancesToRestore
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count() ||
            state.Sunshine is null ||
            !Enum.IsDefined(state.Sunshine.Kind))
        {
            throw new InvalidDataException("The Vita host-feature state contains an invalid value.");
        }
        SunshineBackendController.ValidateSnapshot(state.Sunshine);
    }

    private static bool IsValidDeviceInstanceId(string? instanceId) =>
        !string.IsNullOrWhiteSpace(instanceId) &&
        instanceId.Length <= 512 &&
        !instanceId.Any(char.IsControl);

    private static byte[] ParseSha256(string value)
    {
        if (value.Length != 64)
        {
            throw new InvalidDataException("The lifecycle record checksum has an invalid length.");
        }
        return Convert.FromHexString(value);
    }

    private static string SerializeState(BackendPersistedState state) =>
        JsonSerializer.Serialize(state, JsonOptions);

    private static string ComputeSha256(string payload) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));

    private static string? NormalizeError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return null;
        var normalized = string.Join(
            " ",
            error.Split(
                ['\r', '\n', '\t'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return normalized.Length <= 2048 ? normalized : normalized[..2048];
    }

    private static string JoinErrors(params string?[] errors) =>
        string.Join(" ", errors.Where(error => !string.IsNullOrWhiteSpace(error)));

    private sealed record StateCopy(
        bool Exists,
        BackendPersistedState? State,
        string? Error);
}
