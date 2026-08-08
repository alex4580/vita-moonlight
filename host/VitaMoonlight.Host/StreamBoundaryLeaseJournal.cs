using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VitaMoonlight.Host;

internal enum StreamBoundaryLeasePhase
{
    Prepared,
    Started,
}

internal enum StreamBoundaryLeaseDisposition
{
    Idle,
    LivePrepared,
    LiveStarted,
    Expired,
    OrphanedRecovery,
    MismatchedRecovery,
    StaleLease,
}

internal sealed record StreamBoundaryLeaseState(
    int FormatVersion,
    long Revision,
    StreamBoundaryLeasePhase Phase,
    string Generation,
    string OwnerCertificateSha256,
    int Width,
    int Height,
    int Fps,
    DateTimeOffset RecoveryCapturedAtUtc,
    DateTimeOffset PreparedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? LastHeartbeatAtUtc,
    DateTimeOffset LeaseExpiresAtUtc);

internal sealed record StreamBoundaryLeaseAssessment(
    StreamBoundaryLeaseDisposition Disposition,
    StreamBoundaryLeaseState? Lease,
    DateTimeOffset? DurableRecoveryCapturedAtUtc,
    DateTimeOffset AssessedAtUtc)
{
    internal bool AuthorizesActiveHandoff => Disposition is
        StreamBoundaryLeaseDisposition.LivePrepared or
        StreamBoundaryLeaseDisposition.LiveStarted;

    internal bool RequiresRecovery => Disposition is
        StreamBoundaryLeaseDisposition.Expired or
        StreamBoundaryLeaseDisposition.OrphanedRecovery or
        StreamBoundaryLeaseDisposition.MismatchedRecovery;

    internal bool CanDiscardLeaseAfterRecovery => Disposition is
        StreamBoundaryLeaseDisposition.Expired or
        StreamBoundaryLeaseDisposition.MismatchedRecovery or
        StreamBoundaryLeaseDisposition.StaleLease;
}

/// <summary>
/// Protected, cross-process authority for the exact authenticated Vita that
/// owns a display handoff. The short Prepared lease covers launch negotiation;
/// after /started, periodic authenticated heartbeats are required. The rescue
/// observer can therefore distinguish a live stream from an orphan without
/// treating a Sunshine process shared by other clients as session authority.
/// </summary>
internal static class StreamBoundaryLeaseJournal
{
    internal const int CurrentFormatVersion = 1;
    internal static readonly TimeSpan PreparedLifetime =
        // Prepare itself may spend up to 60 seconds establishing topology,
        // and Sunshine launch negotiation is allowed another 120 seconds.
        // Keep a small scheduling/network margin without granting indefinite
        // authority to a client which never reaches /started.
        TimeSpan.FromSeconds(240);
    internal static readonly TimeSpan StartedLifetime =
        TimeSpan.FromSeconds(45);
    internal static readonly TimeSpan HeartbeatCheckpointInterval =
        TimeSpan.FromSeconds(20);

    private const int MaximumStateBytes = 32 * 1024;
    private const int Sha256HexCharacters = 64;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() },
    };
    private static StreamBoundaryLeaseState? cachedState;
    private static DateTimeOffset? lastCheckpointUtc;

    internal static string StateFile => Path.Combine(
        HostStatePaths.Root,
        "stream-boundary-lease.json");

    internal static string BackupFile => Path.Combine(
        HostStatePaths.Root,
        "stream-boundary-lease.backup.json");

    internal static string LockFile => Path.Combine(
        HostStatePaths.Root,
        "stream-boundary-lease.lock");

    internal static StreamBoundaryLeaseAssessment Assess(
        DateTimeOffset? durableRecoveryCapturedAtUtc,
        DateTimeOffset? nowUtc = null)
    {
        using var lease = AcquireLock();
        return Classify(
            LoadCore(),
            durableRecoveryCapturedAtUtc,
            NormalizeUtc(nowUtc ?? DateTimeOffset.UtcNow));
    }

    internal static StreamBoundaryLeaseState PublishPrepared(
        string generation,
        ReadOnlySpan<byte> ownerCertificateHash,
        int width,
        int height,
        int fps,
        DateTimeOffset recoveryCapturedAtUtc,
        DateTimeOffset? nowUtc = null)
    {
        using var lease = AcquireLock();
        var existing = LoadCore();
        if (existing is not null)
        {
            throw new InvalidOperationException(
                "A protected Vita stream-boundary lease already exists.");
        }
        var created = CreatePrepared(
            generation,
            ownerCertificateHash,
            width,
            height,
            fps,
            recoveryCapturedAtUtc,
            nowUtc ?? DateTimeOffset.UtcNow,
            revision: 1);
        SaveCore(created);
        return created;
    }

    internal static StreamBoundaryLeaseState MarkStarted(
        string generation,
        ReadOnlySpan<byte> ownerCertificateHash,
        DateTimeOffset expectedRecoveryCapturedAtUtc,
        DateTimeOffset? nowUtc = null)
    {
        using var lease = AcquireLock();
        var current = RequireExactAuthority(
            LoadCore(),
            generation,
            ownerCertificateHash,
            expectedRecoveryCapturedAtUtc);
        var updated = TransitionStarted(
            current,
            NormalizeUtc(nowUtc ?? DateTimeOffset.UtcNow));
        if (current.Phase == StreamBoundaryLeasePhase.Prepared ||
            ShouldCheckpointHeartbeat(
                lastCheckpointUtc,
                updated.LastHeartbeatAtUtc!.Value))
        {
            SaveCore(updated);
        }
        else
        {
            CacheCore(updated);
        }
        return updated;
    }

    internal static StreamBoundaryLeaseState RenewHeartbeat(
        string generation,
        ReadOnlySpan<byte> ownerCertificateHash,
        DateTimeOffset expectedRecoveryCapturedAtUtc,
        DateTimeOffset? nowUtc = null)
    {
        using var lease = AcquireLock();
        var current = RequireExactAuthority(
            LoadCore(),
            generation,
            ownerCertificateHash,
            expectedRecoveryCapturedAtUtc);
        var updated = Renew(
            current,
            NormalizeUtc(nowUtc ?? DateTimeOffset.UtcNow));
        if (ShouldCheckpointHeartbeat(
                lastCheckpointUtc,
                updated.LastHeartbeatAtUtc!.Value))
        {
            SaveCore(updated);
        }
        else
        {
            CacheCore(updated);
        }
        return updated;
    }

    internal static void RemoveExact(
        string generation,
        ReadOnlySpan<byte> ownerCertificateHash,
        DateTimeOffset expectedRecoveryCapturedAtUtc)
    {
        using var lease = AcquireLock();
        _ = RequireExactAuthority(
            LoadCore(),
            generation,
            ownerCertificateHash,
            expectedRecoveryCapturedAtUtc);
        DeleteCore();
    }

    /// <summary>
    /// Removes an assessed lease only if it is still the same revision and
    /// exact recovery identity. Call this after the matching recovery record
    /// was restored, or for a lease proven stale because no recovery exists.
    /// </summary>
    internal static bool RemoveAssessed(
        StreamBoundaryLeaseAssessment assessment)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        if (!assessment.CanDiscardLeaseAfterRecovery ||
            assessment.Lease is not { } expected)
        {
            return false;
        }

        using var lease = AcquireLock();
        var current = LoadCore();
        if (!MatchesAssessedRevision(current, expected))
        {
            return false;
        }
        DeleteCore();
        return true;
    }

    /// <summary>
    /// Invalidates a still-live lease only after the observer independently
    /// proved that Sunshine exited or its authoritative global active-session
    /// count reached zero. The revision compare makes a concurrent heartbeat
    /// win safely: the observer must re-assess instead of deleting renewed
    /// client authority.
    /// </summary>
    internal static bool RemoveAssessedForProvenNoSession(
        StreamBoundaryLeaseAssessment assessment)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        if (!assessment.AuthorizesActiveHandoff ||
            assessment.Lease is not { } expected)
        {
            return false;
        }
        using var lease = AcquireLock();
        var current = LoadCore();
        if (!MatchesAssessedRevision(current, expected))
        {
            return false;
        }
        DeleteCore();
        return true;
    }

    internal static bool CanRemoveAssessedForProvenNoSessionForTest(
        StreamBoundaryLeaseState? current,
        StreamBoundaryLeaseAssessment assessment) =>
        assessment.AuthorizesActiveHandoff &&
        assessment.Lease is { } expected &&
        MatchesAssessedRevision(current, expected);

    internal static bool MatchesAuthority(
        StreamBoundaryLeaseState state,
        string generation,
        ReadOnlySpan<byte> ownerCertificateHash,
        DateTimeOffset expectedRecoveryCapturedAtUtc)
    {
        Validate(state);
        if (!StreamBoundaryBridgeServer.IsGeneration(generation) ||
            ownerCertificateHash.Length != SHA256.HashSizeInBytes)
        {
            return false;
        }
        var expectedHash = Convert.FromHexString(
            state.OwnerCertificateSha256);
        return FixedTimeTextEquals(state.Generation, generation) &&
            CryptographicOperations.FixedTimeEquals(
                expectedHash,
                ownerCertificateHash) &&
            state.RecoveryCapturedAtUtc.UtcTicks ==
                expectedRecoveryCapturedAtUtc.UtcTicks;
    }

    internal static StreamBoundaryLeaseAssessment ClassifyForTest(
        StreamBoundaryLeaseState? state,
        DateTimeOffset? durableRecoveryCapturedAtUtc,
        DateTimeOffset nowUtc) =>
        Classify(state, durableRecoveryCapturedAtUtc, NormalizeUtc(nowUtc));

    internal static StreamBoundaryLeaseState CreatePreparedForTest(
        string generation,
        ReadOnlySpan<byte> ownerCertificateHash,
        int width,
        int height,
        int fps,
        DateTimeOffset recoveryCapturedAtUtc,
        DateTimeOffset nowUtc) =>
        CreatePrepared(
            generation,
            ownerCertificateHash,
            width,
            height,
            fps,
            recoveryCapturedAtUtc,
            nowUtc,
            revision: 1);

    internal static StreamBoundaryLeaseState MarkStartedForTest(
        StreamBoundaryLeaseState state,
        DateTimeOffset nowUtc) =>
        TransitionStarted(state, NormalizeUtc(nowUtc));

    internal static StreamBoundaryLeaseState RenewHeartbeatForTest(
        StreamBoundaryLeaseState state,
        DateTimeOffset nowUtc) =>
        Renew(state, NormalizeUtc(nowUtc));

    internal static bool ShouldCheckpointHeartbeatForTest(
        DateTimeOffset? lastCheckpoint,
        DateTimeOffset nowUtc) =>
        ShouldCheckpointHeartbeat(
            lastCheckpoint?.ToUniversalTime(),
            NormalizeUtc(nowUtc));

    private static StreamBoundaryLeaseState CreatePrepared(
        string generation,
        ReadOnlySpan<byte> ownerCertificateHash,
        int width,
        int height,
        int fps,
        DateTimeOffset recoveryCapturedAtUtc,
        DateTimeOffset nowUtc,
        long revision)
    {
        var preparedAt = NormalizeUtc(nowUtc);
        var state = new StreamBoundaryLeaseState(
            CurrentFormatVersion,
            revision,
            StreamBoundaryLeasePhase.Prepared,
            generation,
            Convert.ToHexString(ownerCertificateHash).ToLowerInvariant(),
            width,
            height,
            fps,
            NormalizeUtc(recoveryCapturedAtUtc),
            preparedAt,
            null,
            null,
            preparedAt.Add(PreparedLifetime));
        Validate(state);
        return state;
    }

    private static StreamBoundaryLeaseState TransitionStarted(
        StreamBoundaryLeaseState state,
        DateTimeOffset nowUtc)
    {
        Validate(state);
        RequireLive(state, nowUtc);
        var startedAt = state.StartedAtUtc ?? nowUtc;
        if (nowUtc < startedAt)
        {
            throw new InvalidDataException(
                "The stream-boundary clock moved before the recorded start time.");
        }
        var updated = state with
        {
            Revision = checked(state.Revision + 1),
            Phase = StreamBoundaryLeasePhase.Started,
            StartedAtUtc = startedAt,
            LastHeartbeatAtUtc = nowUtc,
            LeaseExpiresAtUtc = nowUtc.Add(StartedLifetime),
        };
        Validate(updated);
        return updated;
    }

    private static StreamBoundaryLeaseState Renew(
        StreamBoundaryLeaseState state,
        DateTimeOffset nowUtc)
    {
        Validate(state);
        if (state.Phase != StreamBoundaryLeasePhase.Started)
        {
            throw new InvalidOperationException(
                "A prepared Vita launch must report started before sending heartbeats.");
        }
        RequireLive(state, nowUtc);
        if (nowUtc < state.LastHeartbeatAtUtc)
        {
            throw new InvalidDataException(
                "The stream-boundary clock moved before the previous heartbeat.");
        }
        var updated = state with
        {
            Revision = checked(state.Revision + 1),
            LastHeartbeatAtUtc = nowUtc,
            LeaseExpiresAtUtc = nowUtc.Add(StartedLifetime),
        };
        Validate(updated);
        return updated;
    }

    private static StreamBoundaryLeaseAssessment Classify(
        StreamBoundaryLeaseState? state,
        DateTimeOffset? durableRecoveryCapturedAtUtc,
        DateTimeOffset nowUtc)
    {
        if (state is not null) Validate(state);
        var durable = durableRecoveryCapturedAtUtc?.ToUniversalTime();
        if (state is null)
        {
            return new StreamBoundaryLeaseAssessment(
                durable is null
                    ? StreamBoundaryLeaseDisposition.Idle
                    : StreamBoundaryLeaseDisposition.OrphanedRecovery,
                null,
                durable,
                nowUtc);
        }
        if (durable is null)
        {
            return new StreamBoundaryLeaseAssessment(
                StreamBoundaryLeaseDisposition.StaleLease,
                state,
                null,
                nowUtc);
        }
        if (state.RecoveryCapturedAtUtc.UtcTicks != durable.Value.UtcTicks)
        {
            return new StreamBoundaryLeaseAssessment(
                StreamBoundaryLeaseDisposition.MismatchedRecovery,
                state,
                durable,
                nowUtc);
        }
        if (nowUtc >= state.LeaseExpiresAtUtc)
        {
            return new StreamBoundaryLeaseAssessment(
                StreamBoundaryLeaseDisposition.Expired,
                state,
                durable,
                nowUtc);
        }
        return new StreamBoundaryLeaseAssessment(
            state.Phase == StreamBoundaryLeasePhase.Prepared
                ? StreamBoundaryLeaseDisposition.LivePrepared
                : StreamBoundaryLeaseDisposition.LiveStarted,
            state,
            durable,
            nowUtc);
    }

    private static StreamBoundaryLeaseState RequireExactAuthority(
        StreamBoundaryLeaseState? state,
        string generation,
        ReadOnlySpan<byte> ownerCertificateHash,
        DateTimeOffset expectedRecoveryCapturedAtUtc)
    {
        if (state is null ||
            !MatchesAuthority(
                state,
                generation,
                ownerCertificateHash,
                expectedRecoveryCapturedAtUtc))
        {
            throw new InvalidOperationException(
                "The authenticated generation does not own the protected display handoff lease.");
        }
        return state;
    }

    private static void RequireLive(
        StreamBoundaryLeaseState state,
        DateTimeOffset nowUtc)
    {
        if (nowUtc >= state.LeaseExpiresAtUtc)
        {
            throw new TimeoutException(
                "The authenticated stream-boundary lease has expired.");
        }
    }

    private static FileStream AcquireLock()
    {
        MachineStateSecurity.RequireStreamBoundaryLeaseAccess();
        try
        {
            return TrustedFileSystem.OpenExclusiveFile(LockFile);
        }
        catch (Exception error) when (
            error is IOException or
                System.ComponentModel.Win32Exception)
        {
            throw new TimeoutException(
                "Another stream-boundary lease operation is still running.",
                error);
        }
    }

    private static StreamBoundaryLeaseState? LoadCore()
    {
        var persisted = LoadPersistentCore();
        if (cachedState is null ||
            persisted is not null &&
            persisted.Revision > cachedState.Revision)
        {
            cachedState = persisted;
            lastCheckpointUtc = persisted?.LastHeartbeatAtUtc ??
                persisted?.PreparedAtUtc;
        }
        else if (persisted is not null &&
            persisted.Revision == cachedState.Revision &&
            !Equals(persisted, cachedState))
        {
            throw new InvalidDataException(
                "The in-memory and durable stream-boundary lease conflict at the same revision.");
        }
        return cachedState;
    }

    private static StreamBoundaryLeaseState? LoadPersistentCore()
    {
        var primary = TryRead(StateFile);
        var backup = TryRead(BackupFile);
        if (!primary.Exists && !backup.Exists) return null;
        var valid = new[] { primary, backup }
            .Where(candidate => candidate.State is not null)
            .Select(candidate => candidate.State!)
            .OrderByDescending(state => state.Revision)
            .ToArray();
        if (valid.Length == 0)
        {
            throw new InvalidDataException(
                "Both protected stream-boundary lease records are unreadable.");
        }
        if (primary.State is { } first && backup.State is { } second &&
            first.Revision == second.Revision && !Equals(first, second))
        {
            throw new InvalidDataException(
                "The protected stream-boundary lease records conflict at the same revision.");
        }
        return valid[0];
    }

    private static LeaseReadCandidate TryRead(string path)
    {
        if (!File.Exists(path)) return new LeaseReadCandidate(false, null);
        try
        {
            var information = new FileInfo(path);
            if (information.Length <= 0 ||
                information.Length > MaximumStateBytes)
            {
                return new LeaseReadCandidate(true, null);
            }
            var state = JsonSerializer.Deserialize<StreamBoundaryLeaseState>(
                TrustedFileSystem.ReadAllText(path),
                JsonOptions);
            if (state is null) return new LeaseReadCandidate(true, null);
            Validate(state);
            return new LeaseReadCandidate(true, state);
        }
        catch (Exception error) when (
            StreamBoundaryBridgeServer.IsOperationalRequestFailure(error))
        {
            return new LeaseReadCandidate(true, null);
        }
    }

    private static void SaveCore(StreamBoundaryLeaseState state)
    {
        Validate(state);
        var json = JsonSerializer.Serialize(state, JsonOptions);
        // The newest valid redundant copy wins. A power loss during either
        // flush therefore leaves the other complete revision available.
        TrustedFileSystem.WriteAllText(BackupFile, json);
        TrustedFileSystem.WriteAllText(StateFile, json);
        cachedState = state;
        lastCheckpointUtc = state.LastHeartbeatAtUtc ?? state.PreparedAtUtc;
    }

    private static void CacheCore(StreamBoundaryLeaseState state)
    {
        Validate(state);
        cachedState = state;
    }

    private static void DeleteCore()
    {
        TrustedFileSystem.DeleteFile(StateFile);
        TrustedFileSystem.DeleteFile(BackupFile);
        cachedState = null;
        lastCheckpointUtc = null;
    }

    private static void Validate(StreamBoundaryLeaseState state)
    {
        var preparedUtc = NormalizeUtc(state.PreparedAtUtc);
        var recoveryUtc = NormalizeUtc(state.RecoveryCapturedAtUtc);
        var expiresUtc = NormalizeUtc(state.LeaseExpiresAtUtc);
        if (state.FormatVersion != CurrentFormatVersion ||
            state.Revision <= 0 ||
            !Enum.IsDefined(state.Phase) ||
            !StreamBoundaryBridgeServer.IsGeneration(state.Generation) ||
            !IsLowerHex(
                state.OwnerCertificateSha256,
                Sha256HexCharacters) ||
            state.Width <= 0 || state.Height <= 0 || state.Fps <= 0 ||
            state.Width > ushort.MaxValue ||
            state.Height > ushort.MaxValue ||
            state.Fps > 1000 ||
            state.RecoveryCapturedAtUtc.Offset != TimeSpan.Zero ||
            state.PreparedAtUtc.Offset != TimeSpan.Zero ||
            state.LeaseExpiresAtUtc.Offset != TimeSpan.Zero ||
            recoveryUtc == default || preparedUtc == default ||
            expiresUtc <= preparedUtc)
        {
            throw new InvalidDataException(
                "The protected stream-boundary lease has an unsupported structure.");
        }
        if (state.Phase == StreamBoundaryLeasePhase.Prepared)
        {
            if (state.StartedAtUtc is not null ||
                state.LastHeartbeatAtUtc is not null ||
                expiresUtc != preparedUtc.Add(PreparedLifetime))
            {
                throw new InvalidDataException(
                    "The protected prepared stream-boundary lease is inconsistent.");
            }
            return;
        }

        if (state.StartedAtUtc is not { } started ||
            state.LastHeartbeatAtUtc is not { } heartbeat ||
            started.Offset != TimeSpan.Zero ||
            heartbeat.Offset != TimeSpan.Zero ||
            started < preparedUtc || heartbeat < started ||
            expiresUtc != heartbeat.Add(StartedLifetime))
        {
            throw new InvalidDataException(
                "The protected started stream-boundary lease is inconsistent.");
        }
    }

    private static bool IsLowerHex(string? value, int length) =>
        value is not null && value.Length == length &&
        value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool FixedTimeTextEquals(string left, string right) =>
        left.Length == right.Length &&
        CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(left),
            Encoding.ASCII.GetBytes(right));

    private static bool MatchesAssessedRevision(
        StreamBoundaryLeaseState? current,
        StreamBoundaryLeaseState expected) =>
        current is not null &&
        current.Revision == expected.Revision &&
        FixedTimeTextEquals(current.Generation, expected.Generation) &&
        current.RecoveryCapturedAtUtc.UtcTicks ==
            expected.RecoveryCapturedAtUtc.UtcTicks;

    private static bool ShouldCheckpointHeartbeat(
        DateTimeOffset? lastCheckpoint,
        DateTimeOffset nowUtc) =>
        lastCheckpoint is null ||
        nowUtc - lastCheckpoint.Value >= HeartbeatCheckpointInterval;

    private static DateTimeOffset NormalizeUtc(DateTimeOffset value) =>
        value.ToUniversalTime();

    private sealed record LeaseReadCandidate(
        bool Exists,
        StreamBoundaryLeaseState? State);
}
