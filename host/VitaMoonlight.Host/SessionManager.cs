using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace VitaMoonlight.Host;

internal sealed record SessionStartResult(
    string DisplayName,
    int Width,
    int Height,
    int StreamFps,
    int DesktopRefreshRate,
    string RecoveryFile,
    DateTimeOffset RecoveryCapturedAt);
internal sealed record SessionModeResult(string DisplayName, VitaDisplayMode Mode);

internal sealed record DisplaySuspendIntentState(
    int FormatVersion,
    string Token,
    int OwnerProcessId,
    DateTimeOffset OwnerStartedAtUtc,
    DateTimeOffset RequestedAtUtc);

internal enum DisplaySuspendIntentDisposition
{
    Missing,
    Active,
    Stale,
}

internal sealed record DisplaySuspendIntentInspection(
    DisplaySuspendIntentDisposition Disposition,
    DisplaySuspendIntentState? State,
    string? Detail);

internal sealed class DisplaySuspendInterruptedException :
    InvalidOperationException
{
    internal DisplaySuspendInterruptedException(string message) :
        base(message)
    {
    }
}

/// <summary>
/// A durable cross-process fence published before the recovery agent
/// acknowledges suspend. Display commands sample it before waiting for the
/// display lease and again while holding that lease, so a session which raced
/// the power event can never commit a managed-VDD topology afterward.
/// </summary>
internal static class DisplaySuspendIntentStore
{
    private const int CurrentFormatVersion = 1;
    private const int MaximumStateBytes = 16 * 1024;
    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(2);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    internal static string IntentFile => Path.Combine(
        HostStatePaths.Root,
        "display-suspend.intent");

    internal static string GateFile => Path.Combine(
        HostStatePaths.Root,
        "display-suspend.lock");

    internal static DisplaySuspendIntentState BeginForCurrentAgent()
    {
        using var gate = AcquireGate();
        SecureIntentFile();
        var existing = InspectLocked();
        using var current = Process.GetCurrentProcess();
        var ownerStartedAtUtc = new DateTimeOffset(
            current.StartTime.ToUniversalTime(),
            TimeSpan.Zero);
        if (existing.Disposition == DisplaySuspendIntentDisposition.Active &&
            existing.State is { } active &&
            (active.OwnerProcessId != Environment.ProcessId ||
             active.OwnerStartedAtUtc.UtcTicks != ownerStartedAtUtc.UtcTicks))
        {
            throw new InvalidOperationException(
                $"Another live recovery agent ({active.OwnerProcessId}) already owns the Windows suspend fence.");
        }

        var state = new DisplaySuspendIntentState(
            CurrentFormatVersion,
            Guid.NewGuid().ToString("N"),
            Environment.ProcessId,
            ownerStartedAtUtc,
            DateTimeOffset.UtcNow);
        Validate(state);
        TrustedFileSystem.WriteAllText(
            IntentFile,
            JsonSerializer.Serialize(state, JsonOptions));
        return state;
    }

    internal static DisplaySuspendIntentInspection Inspect()
    {
        using var gate = AcquireGate();
        SecureIntentFile();
        return InspectLocked();
    }

    internal static bool ClearOwnedAfterPhysicalResumeLocked(
        DisplayTransactionLease transaction,
        DisplaySuspendIntentState expected)
    {
        transaction.RequireActive();
        using var gate = AcquireGate();
        SecureIntentFile();
        return ClearOwnedLocked(expected);
    }

    private static bool ClearOwnedLocked(
        DisplaySuspendIntentState expected)
    {
        var current = InspectLocked();
        if (current.State is null ||
            !string.Equals(
                current.State.Token,
                expected.Token,
                StringComparison.Ordinal))
        {
            return false;
        }
        return TrustedFileSystem.DeleteFile(IntentFile);
    }

    internal static void RequireClearForDisplayMutationLocked(
        DisplayTransactionLease transaction,
        DisplaySuspendIntentInspection beforeLease,
        string operation)
    {
        transaction.RequireActive();
        var whileLocked = InspectWhileDisplayTransactionHeld(transaction);
        if (!BlocksDisplayMutation(
                beforeLease.Disposition,
                whileLocked.Disposition))
        {
            return;
        }

        RecoverPhysicalForBlockedMutationLocked(transaction, operation);
        if (whileLocked.Disposition == DisplaySuspendIntentDisposition.Stale)
        {
            ClearIfStillStaleAfterPhysicalRecovery(transaction);
        }
        throw CreateBlockedMutationError(
            operation,
            beforeLease,
            whileLocked);
    }

    internal static void RequireClearAfterDisplayMutationLocked(
        DisplayTransactionLease transaction,
        string operation)
    {
        transaction.RequireActive();
        var current = InspectWhileDisplayTransactionHeld(transaction);
        if (current.Disposition == DisplaySuspendIntentDisposition.Missing)
        {
            return;
        }

        RecoverPhysicalForBlockedMutationLocked(transaction, operation);
        if (current.Disposition == DisplaySuspendIntentDisposition.Stale)
        {
            ClearIfStillStaleAfterPhysicalRecovery(transaction);
        }
        throw new DisplaySuspendInterruptedException(
            $"{operation} was cancelled because Windows began suspending while the display was changing. " +
            "The physical display was restored; retry after Windows has fully resumed.");
    }

    internal static DisplaySuspendIntentDisposition ClassifyForTest(
        bool recordExists,
        DisplaySuspendIntentState? state,
        bool? ownerIsAlive) =>
        Classify(recordExists, state, ownerIsAlive);

    internal static bool BlocksDisplayMutationForTest(
        DisplaySuspendIntentDisposition beforeLease,
        DisplaySuspendIntentDisposition whileLocked) =>
        BlocksDisplayMutation(beforeLease, whileLocked);

    internal static bool TryRecoverStaleAtAgentStartup(
        TimeSpan displayLeaseTimeout,
        out string message)
    {
        var initial = Inspect();
        if (initial.Disposition == DisplaySuspendIntentDisposition.Missing)
        {
            message = "No interrupted Windows suspend fence was present.";
            return false;
        }
        if (initial.Disposition == DisplaySuspendIntentDisposition.Active)
        {
            message =
                "A live recovery agent still owns the Windows suspend fence; kept the physical-safety block in place.";
            return false;
        }

        using var transaction = DisplayTransactionLock.AcquireWithin(
            displayLeaseTimeout);
        var confirmed = InspectWhileDisplayTransactionHeld(transaction);
        if (confirmed.Disposition == DisplaySuspendIntentDisposition.Missing)
        {
            message = "The interrupted Windows suspend fence was already cleared.";
            return false;
        }
        if (confirmed.Disposition == DisplaySuspendIntentDisposition.Active)
        {
            message =
                "A new live Windows suspend fence appeared while startup recovery was waiting; left it in place.";
            return false;
        }

        var recovery = UninstallManager
            .RecoverPhysicalAndDiscardPendingTransactionLocked(transaction);
        ClearIfStillStaleAfterPhysicalRecovery(transaction);
        message =
            $"Recovered stale Windows suspend state to physical display(s) " +
            $"{string.Join(", ", recovery.PhysicalDisplays)} and cleared its exact fence.";
        return true;
    }

    private static void RecoverPhysicalForBlockedMutationLocked(
        DisplayTransactionLease transaction,
        string operation)
    {
        try
        {
            UninstallManager.RecoverPhysicalAndDiscardPendingTransactionLocked(
                transaction);
        }
        catch (Exception error)
        {
            throw new InvalidOperationException(
                $"{operation} was blocked by Windows suspend, but the physical display could not be reverified. " +
                "The suspend fence remains active; use the display recovery hotkey after resume.",
                error);
        }
    }

    private static DisplaySuspendInterruptedException CreateBlockedMutationError(
        string operation,
        DisplaySuspendIntentInspection beforeLease,
        DisplaySuspendIntentInspection whileLocked)
    {
        var stale = beforeLease.Disposition ==
                        DisplaySuspendIntentDisposition.Stale ||
                    whileLocked.Disposition ==
                        DisplaySuspendIntentDisposition.Stale;
        return new DisplaySuspendInterruptedException(stale
            ? $"{operation} found an interrupted Windows suspend. The physical display was recovered and the stale fence was cleared; retry the operation."
            : $"{operation} was cancelled because Windows is suspending. The physical display was restored; retry after Windows has fully resumed.");
    }

    private static bool BlocksDisplayMutation(
        DisplaySuspendIntentDisposition beforeLease,
        DisplaySuspendIntentDisposition whileLocked) =>
        beforeLease != DisplaySuspendIntentDisposition.Missing ||
        whileLocked != DisplaySuspendIntentDisposition.Missing;

    private static DisplaySuspendIntentInspection
        InspectWhileDisplayTransactionHeld(
            DisplayTransactionLease transaction)
    {
        transaction.RequireActive();
        using var gate = AcquireGate();
        SecureIntentFile();
        return InspectLocked();
    }

    private static void ClearIfStillStaleAfterPhysicalRecovery(
        DisplayTransactionLease transaction)
    {
        transaction.RequireActive();
        using var gate = AcquireGate();
        SecureIntentFile();
        if (InspectLocked().Disposition == DisplaySuspendIntentDisposition.Stale)
        {
            TrustedFileSystem.DeleteFile(IntentFile);
        }
    }

    private static DisplaySuspendIntentInspection InspectLocked()
    {
        if (!File.Exists(IntentFile))
        {
            return new DisplaySuspendIntentInspection(
                DisplaySuspendIntentDisposition.Missing,
                null,
                null);
        }

        DisplaySuspendIntentState? state = null;
        string? detail = null;
        try
        {
            var information = new FileInfo(IntentFile);
            if (information.Length <= 0 ||
                information.Length > MaximumStateBytes)
            {
                throw new InvalidDataException(
                    "The Windows suspend fence has an invalid size.");
            }
            state = JsonSerializer.Deserialize<DisplaySuspendIntentState>(
                TrustedFileSystem.ReadAllText(IntentFile),
                JsonOptions);
            if (state is null)
            {
                throw new InvalidDataException(
                    "The Windows suspend fence is empty.");
            }
            Validate(state);
        }
        catch (Exception error) when (
            error is JsonException or
                IOException or
                InvalidDataException or
                UnauthorizedAccessException or
                Win32Exception)
        {
            state = null;
            detail = error.Message;
        }

        var ownerIsAlive = state is null
            ? false
            : IsRecordedOwnerAlive(state);
        var disposition = Classify(
            recordExists: true,
            state,
            ownerIsAlive);
        return new DisplaySuspendIntentInspection(
            disposition,
            state,
            detail);
    }

    private static DisplaySuspendIntentDisposition Classify(
        bool recordExists,
        DisplaySuspendIntentState? state,
        bool? ownerIsAlive)
    {
        if (!recordExists)
        {
            return DisplaySuspendIntentDisposition.Missing;
        }
        if (state is null)
        {
            return DisplaySuspendIntentDisposition.Stale;
        }
        return ownerIsAlive == false
            ? DisplaySuspendIntentDisposition.Stale
            : DisplaySuspendIntentDisposition.Active;
    }

    private static bool? IsRecordedOwnerAlive(
        DisplaySuspendIntentState state)
    {
        try
        {
            using var process = Process.GetProcessById(state.OwnerProcessId);
            var started = process.StartTime.ToUniversalTime();
            return !process.HasExited &&
                state.OwnerStartedAtUtc.UtcTicks == started.Ticks;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Win32Exception)
        {
            // Unknown is active: only a proven-dead owner may be cleared.
            return null;
        }
    }

    private static void Validate(DisplaySuspendIntentState state)
    {
        if (state.FormatVersion != CurrentFormatVersion ||
            !Guid.TryParseExact(state.Token, "N", out _) ||
            state.OwnerProcessId <= 0 ||
            state.OwnerStartedAtUtc == default ||
            state.RequestedAtUtc == default ||
            state.OwnerStartedAtUtc > state.RequestedAtUtc.AddMinutes(1))
        {
            throw new InvalidDataException(
                "The protected Windows suspend fence contains an unsupported value.");
        }
    }

    private static IDisposable AcquireGate()
    {
        MachineStateSecurity.SecureContainer();
        var elapsed = Stopwatch.StartNew();
        Exception? lastContention = null;
        do
        {
            try
            {
                return TrustedFileSystem.OpenExclusiveFile(GateFile);
            }
            catch (Exception error) when (IsGateContention(error))
            {
                lastContention = error;
            }

            var remaining = GateTimeout - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero) break;
            Thread.Sleep(remaining < TimeSpan.FromMilliseconds(25)
                ? remaining
                : TimeSpan.FromMilliseconds(25));
        }
        while (elapsed.Elapsed < GateTimeout);

        throw new TimeoutException(
            "Another process is updating the protected Windows suspend fence.",
            lastContention);
    }

    private static void SecureIntentFile()
    {
        MachineStateSecurity.SecureContainer();
        TrustedFileSystem.SecureExistingFile(IntentFile);
    }

    private static bool IsGateContention(Exception error)
    {
        if (error is Win32Exception windowsError &&
            windowsError.NativeErrorCode is 32 or 33)
        {
            return true;
        }
        if (error is IOException ioError &&
            (ioError.HResult & 0xffff) is 32 or 33)
        {
            return true;
        }
        return error.InnerException is not null &&
            IsGateContention(error.InnerException);
    }
}

/// <summary>
/// Process-wide proof that the caller owns the machine display transaction.
/// Every operation which changes the active topology or the pending recovery
/// record must hold one of these leases for its complete read/check/write
/// sequence. Requiring the typed lease on locked core methods makes accidental
/// nested acquisition visible at the call site.
/// </summary>
internal sealed class DisplayTransactionLease : IDisposable
{
    private FileStream? stream;

    internal DisplayTransactionLease(FileStream stream)
    {
        this.stream = stream;
    }

    internal void RequireActive()
    {
        if (stream is null)
        {
            throw new ObjectDisposedException(
                nameof(DisplayTransactionLease),
                "The display transaction lease is no longer active.");
        }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref stream, null)?.Dispose();
    }
}

internal static class DisplayTransactionLock
{
    internal static DisplayTransactionLease Acquire()
    {
        MachineStateSecurity.Secure();
        return OpenSecuredTransactionFile();
    }

    internal static DisplayTransactionLease AcquireWithin(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero || timeout > TimeSpan.FromSeconds(10))
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                "The bounded display-transaction wait must be between zero and ten seconds.");
        }

        var elapsed = Stopwatch.StartNew();
        Exception? lastContention = null;
        do
        {
            try
            {
                return Acquire();
            }
            catch (Exception error) when (IsTransactionContention(error))
            {
                lastContention = error;
            }

            var remaining = timeout - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero) break;
            Thread.Sleep(remaining < TimeSpan.FromMilliseconds(50)
                ? remaining
                : TimeSpan.FromMilliseconds(50));
        }
        while (elapsed.Elapsed < timeout);

        throw new TimeoutException(
            $"Another Vita Moonlight display transaction remained active for the {timeout.TotalMilliseconds:0} ms safety wait.",
            lastContention);
    }

    internal static DisplayTransactionLease AcquireForRecoveryUpgradeOnly()
    {
        InstallationTrust.RequireInstalledPayload(
            "Migrating legacy display recovery state");
        if (MachineStateSecurity.IsProtectionInitialized())
        {
            return Acquire();
        }

        // session recover-upgrade is the sole bootstrap path before the v2
        // protection marker exists. The operational CLI has already removed
        // state-directory overrides, and installation trust pins this to the
        // installed Program Files payload. Secure the fixed container before
        // creating its first transaction file.
        MachineStateSecurity.SecureContainer();
        return OpenSecuredTransactionFile();
    }

    internal static DisplayTransactionLease
        AcquireForInstallerMaintenanceBootstrap(int ownerProcessId)
    {
        InstallerMaintenanceFence.RequireBootstrapHelper(ownerProcessId);
        if (MachineStateSecurity.IsProtectionInitialized())
        {
            return Acquire();
        }

        // The packaged maintenance helper is the only portable executable
        // allowed to bootstrap a legacy registered installation. It owns the
        // live setup PID and the installer command gate before this method is
        // reached. No legacy state path is trusted or applied.
        MachineStateSecurity.SecureContainer();
        return OpenSecuredTransactionFile();
    }

    private static DisplayTransactionLease OpenSecuredTransactionFile()
    {
        try
        {
            return new DisplayTransactionLease(
                TrustedFileSystem.OpenExclusiveFile(
                    HostStatePaths.LockFile));
        }
        catch (IOException error)
        {
            throw new InvalidOperationException(
                "Another Vita Moonlight display transaction is already running.",
                error);
        }
        catch (System.ComponentModel.Win32Exception error)
            when (error.NativeErrorCode is 32 or 33)
        {
            throw new InvalidOperationException(
                "Another Vita Moonlight display transaction is already running.",
                error);
        }
    }

    private static bool IsTransactionContention(Exception error)
    {
        if (error is Win32Exception windowsError &&
            windowsError.NativeErrorCode is 32 or 33)
        {
            return true;
        }
        if (error is IOException ioError &&
            (ioError.HResult & 0xffff) is 32 or 33)
        {
            return true;
        }
        return error.InnerException is not null &&
            IsTransactionContention(error.InnerException);
    }
}

internal sealed class SessionManager
{
    private readonly DisplayTopologyService displays = new();

    internal SessionStartResult Start(int width, int height, int fps)
    {
        var streamMode = VitaDisplayModes.RequireSupportedStreamMode(
            width,
            height,
            fps);
        return StartValidated(
            streamMode,
            beforeTransactionRelease: null);
    }

    /// <summary>
    /// Establishes the display handoff and publishes its external authority
    /// before releasing the cross-process display transaction. This closes
    /// the recovery-file watcher window in which a newly active display could
    /// otherwise be mistaken for an orphan before its authenticated lease was
    /// durable. A publication failure restores the exact physical snapshot
    /// while the same transaction is still held.
    /// </summary>
    internal SessionStartResult Start(
        int width,
        int height,
        int fps,
        Action<SessionStartResult>? beforeTransactionRelease)
    {
        var streamMode = VitaDisplayModes.RequireSupportedStreamMode(
            width,
            height,
            fps);
        return StartValidated(streamMode, beforeTransactionRelease);
    }

    private SessionStartResult StartValidated(
        VitaStreamMode streamMode,
        Action<SessionStartResult>? beforeTransactionRelease)
    {
        var suspendBeforeLease = DisplaySuspendIntentStore.Inspect();
        using var transaction = DisplayTransactionLock.Acquire();
        DisplaySuspendIntentStore.RequireClearForDisplayMutationLocked(
            transaction,
            suspendBeforeLease,
            "Vita display session start");
        RequireFullyReadyBackendLocked(transaction);
        var started = StartCoreLocked(
            transaction,
            streamMode,
            HostSettings.Load(),
            prepareDriverMode: true,
            persistMode: false,
            activationAttempts: 20);
        if (beforeTransactionRelease is null) return started;
        try
        {
            beforeTransactionRelease(started);
            return started;
        }
        catch (Exception publicationError)
        {
            try
            {
                _ = RestoreIfPendingLocked(
                    transaction,
                    started.RecoveryCapturedAt);
            }
            catch (Exception restoreError)
            {
                throw new AggregateException(
                    "Session authority publication failed and automatic display restoration also failed. The exact recovery record was retained.",
                    publicationError,
                    restoreError);
            }
            throw;
        }
    }

    /// <summary>
    /// Temporarily activates the Vita mode only so trusted installer/driver
    /// maintenance can verify mode advertisement. This is not a stream-ready
    /// path: ordinary Start and ChangeMode always require a fully enabled
    /// lifecycle state inside the display transaction lock.
    /// </summary>
    internal SessionStartResult PrimeNativeModeForDriverMaintenanceOnly(
        bool installDriver = false,
        bool allowExistingDeviceAdoption = false)
    {
        InstallationTrust.RequireInstalledPayload(
            "Verifying virtual-display mode advertisement");
        var suspendBeforeLease = DisplaySuspendIntentStore.Inspect();
        using var transaction = DisplayTransactionLock.Acquire();
        DisplaySuspendIntentStore.RequireClearForDisplayMutationLocked(
            transaction,
            suspendBeforeLease,
            "Virtual-display driver maintenance");
        BackendLifecycleStateStore.RequireNoUninstallInProgress();
        return PrimeNativeModeForDriverMaintenanceOnlyLocked(
            transaction,
            installDriver,
            allowExistingDeviceAdoption);
    }

    internal SessionStartResult PrimeNativeModeForDriverMaintenanceOnlyLocked(
        DisplayTransactionLease transaction,
        bool installDriver = false,
        bool allowExistingDeviceAdoption = false)
    {
        transaction.RequireActive();
        InstallationTrust.RequireInstalledPayload(
            "Verifying virtual-display mode advertisement");
        var mode = VitaDisplayModes.Native;
        var settings = HostSettings.Load() with
        {
            HostMode = "sunshine",
            DisplayMatch = "MTT1337",
            ForceSdr = true,
        };

        var streamMode = VitaDisplayModes.RequireSupportedStreamMode(
            mode.Width,
            mode.Height,
            mode.Fps);
        BackendLifecycleStateStore.RequireNoUninstallInProgress();
        DriverNativeModeVerification.Invalidate();
        if (File.Exists(HostStatePaths.RecoveryFile))
        {
            throw new InvalidOperationException(
                $"A pending display recovery record already exists at {HostStatePaths.RecoveryFile}. Run `session recover` first."
            );
        }

        // Publish exact device authority before any PnP operation. This also
        // completes an interrupted creation when the one new node can still
        // be proven from its durable pre-create inventory. A clean install
        // publishes a creation intent but has no device to stop yet.
        ManagedVddInstallPlan? installPlan = null;
        if (installDriver)
        {
            installPlan = ManagedVddOwnershipJournal.PrepareInstallLocked(
                transaction,
                allowExistingDeviceAdoption);
        }
        if (!installDriver ||
            installPlan?.Action ==
                ManagedVddInstallAction.UseOwnedInstance)
        {
            ManagedVirtualDisplayRuntime.ReconcileIdleLocked(
                transaction,
                requireManagedDevice: true);
        }

        var recovery = displays.CaptureRecovery(mode.Width, mode.Height, mode.Fps);
        displays.SaveRecovery(transaction, recovery);
        DisplayDescriptor selected;
        try
        {
            var wizard = DisplayWizardAdapter.LocateBundled();
            if (installDriver)
            {
                wizard.InstallDriver(
                    transaction,
                    allowExistingDeviceAdoption);
            }
            else
            {
                wizard.ReloadDriver(transaction);
            }
            // The verification topology is intentionally temporary.
            // Persisting a mode for it with CDS_UPDATEREGISTRY can fail even
            // when the active driver advertises and accepts the mode.
            selected = displays.VerifyVirtualDisplayModeSafely(
                settings.DisplayMatch,
                mode.Width,
                mode.Height,
                mode.Fps,
                settings.ForceSdr,
                persistMode: false,
                modeAttempts: 40);
            DisplaySuspendIntentStore.RequireClearAfterDisplayMutationLocked(
                transaction,
                "Virtual-display mode verification");
            // Do not rewrite the recovery record after changing topology.
            // The single pre-switch, disk-flushed snapshot must remain valid
            // even if power is lost immediately after VDD activation.
        }
        catch (DisplaySuspendInterruptedException)
        {
            // The suspend guard already restored and verified physical-only
            // and removed this temporary recovery record.
            throw;
        }
        catch (Exception verificationError)
        {
            try
            {
                ManagedVirtualDisplayRuntime
                    .ReconcileRestoredPhysicalBaselineLocked(
                    transaction,
                    displays.Restore,
                    requireManagedDevice: false);
                DisplayTopologyService.ClearRecovery(transaction);
            }
            catch (Exception restoreError)
            {
                throw new AggregateException(
                    "Native-mode verification failed and automatic display restoration also failed. " +
                    "The recovery record has been retained.",
                    verificationError,
                    restoreError);
            }
            throw new InvalidOperationException(
                $"Native-mode verification failed, and the original physical display layout was restored. " +
                $"{verificationError.Message}",
                verificationError);
        }

        try
        {
            ManagedVirtualDisplayRuntime
                .ReconcileRestoredPhysicalBaselineLocked(
                transaction,
                displays.Restore,
                requireManagedDevice: true);
            DisplayTopologyService.ClearRecovery(transaction);
        }
        catch (Exception restoreError)
        {
            throw new InvalidOperationException(
                "Native mode was verified, but the original physical display layout could not be restored. " +
                "The recovery record has been retained.",
                restoreError);
        }

        // Hash and publish the verification while the same transaction still
        // owns the configuration that Windows just accepted.
        DriverNativeModeVerification.RecordCurrentLocked(transaction);
        return new SessionStartResult(
            selected.FriendlyName,
            mode.Width,
            mode.Height,
            streamMode.StreamFps,
            streamMode.DesktopMode.Fps,
            HostStatePaths.RecoveryFile,
            recovery.CapturedAt);
    }

    private SessionStartResult StartCoreLocked(
        DisplayTransactionLease transaction,
        VitaStreamMode streamMode,
        HostSettings settings,
        bool prepareDriverMode,
        bool persistMode,
        int activationAttempts)
    {
        transaction.RequireActive();
        var desktopMode = streamMode.DesktopMode;
        var usesManagedVitaDisplay = !settings.HostMode.Equals(
            "apollo",
            StringComparison.OrdinalIgnoreCase);
        if (File.Exists(HostStatePaths.RecoveryFile))
        {
            throw new InvalidOperationException(
                $"A pending display recovery record already exists at {HostStatePaths.RecoveryFile}. Run `session recover` first."
            );
        }


        if (usesManagedVitaDisplay)
        {
            ManagedVirtualDisplayRuntime.ReconcileIdleLocked(
                transaction,
                requireManagedDevice: true);
        }

        var recovery = displays.CaptureRecovery(
            desktopMode.Width,
            desktopMode.Height,
            desktopMode.Fps);
        displays.SaveRecovery(transaction, recovery);

        try
        {
            if (prepareDriverMode && usesManagedVitaDisplay)
            {
                DisplayWizardAdapter.LocateBundled().PrepareMode(
                    transaction,
                    desktopMode.Width,
                    desktopMode.Height,
                    desktopMode.Fps);
            }

            var selected = ActivateWithRetry(
                usesManagedVitaDisplay ? null : settings.DisplayMatch,
                desktopMode.Width,
                desktopMode.Height,
                desktopMode.Fps,
                settings.ForceSdr,
                persistMode,
                activationAttempts,
                requireExactVitaTarget: usesManagedVitaDisplay);
            DisplaySuspendIntentStore.RequireClearAfterDisplayMutationLocked(
                transaction,
                "Vita display session start");
            // SelectedDisplay is informational and is never required for
            // restoration. Rewriting the only recovery snapshot after this
            // topology change would create a power-loss window with no valid
            // physical-layout record.
            return new SessionStartResult(
                selected.FriendlyName,
                desktopMode.Width,
                desktopMode.Height,
                streamMode.StreamFps,
                desktopMode.Fps,
                HostStatePaths.RecoveryFile,
                recovery.CapturedAt);
        }
        catch (DisplaySuspendInterruptedException)
        {
            // The suspend guard already restored and verified physical-only
            // and removed the pending session record.
            throw;
        }
        catch (Exception startError)
        {
            try
            {
                displays.Restore();
                if (usesManagedVitaDisplay)
                {
                    ManagedVirtualDisplayRuntime.ReconcileIdleLocked(
                        transaction,
                        requireManagedDevice: true);
                }
                DisplayTopologyService.ClearRecovery(transaction);
            }
            catch (Exception restoreError)
            {
                throw new AggregateException(
                    "Session setup failed and automatic display restoration also failed. The recovery record has been retained.",
                    startError,
                    restoreError);
            }
            throw;
        }
    }

    internal bool RestoreIfPending(
        DateTimeOffset? expectedCapturedAt = null)
    {
        using var transaction = DisplayTransactionLock.Acquire();
        return RestoreIfPendingLocked(transaction, expectedCapturedAt);
    }

    internal bool RecoverToIdle()
    {
        using var transaction = DisplayTransactionLock.Acquire();
        var restored = RestoreIfPendingLocked(transaction);
        if (!restored)
        {
            ManagedVirtualDisplayRuntime.ReconcileIdleLocked(
                transaction,
                requireManagedDevice: false);
        }
        return restored;
    }

    internal bool RestoreIfPendingLocked(
        DisplayTransactionLease transaction,
        DateTimeOffset? expectedCapturedAt = null)
    {
        transaction.RequireActive();
        if (!File.Exists(HostStatePaths.RecoveryFile))
        {
            return false;
        }
        if (expectedCapturedAt is { } expected &&
            displays.LoadRecovery().CapturedAt != expected)
        {
            // A newer transaction owns the current recovery record. A timed
            // test or delayed cleanup must never tear down that session.
            return false;
        }
        ManagedVirtualDisplayRuntime.ReconcileRestoredPhysicalBaselineLocked(
            transaction,
            displays.Restore,
            requireManagedDevice: false);
        DisplayTopologyService.ClearRecovery(transaction);
        return true;
    }

    internal static bool DiscardPendingRecoveryLocked(
        DisplayTransactionLease transaction)
    {
        transaction.RequireActive();
        return DisplayTopologyService.ClearRecovery(transaction);
    }

    internal bool HasPendingRecovery => File.Exists(HostStatePaths.RecoveryFile);

    internal SessionModeResult ChangeMode(int width, int height, int fps)
    {
        var mode = VitaDisplayModes.RequireSupported(width, height, fps);
        var suspendBeforeLease = DisplaySuspendIntentStore.Inspect();
        using var transaction = DisplayTransactionLock.Acquire();
        DisplaySuspendIntentStore.RequireClearForDisplayMutationLocked(
            transaction,
            suspendBeforeLease,
            "Vita virtual-display mode change");
        RequireFullyReadyBackendLocked(transaction);
        var settings = HostSettings.Load();
        if (settings.HostMode.Equals("sunshine", StringComparison.OrdinalIgnoreCase) &&
            !DisplayWizardAdapter.HasVitaCompatibilityModes())
        {
            throw new InvalidOperationException(
                "The Vita display modes are not provisioned. Disconnect the stream and run `driver reload` first.");
        }

        // Do not activate, reload, or disconnect a display here. Changing only
        // the active virtual source mode leaves both Sunshine's disconnect
        // restoration and any companion recovery record intact.
        var selected = displays.ChangeActiveVirtualDisplayMode(
            settings.HostMode.Equals("apollo", StringComparison.OrdinalIgnoreCase)
                ? settings.DisplayMatch
                : null,
            mode.Width,
            mode.Height,
            mode.Fps,
            settings.ForceSdr,
            requireExactVitaTarget: !settings.HostMode.Equals(
                "apollo",
                StringComparison.OrdinalIgnoreCase));
        DisplaySuspendIntentStore.RequireClearAfterDisplayMutationLocked(
            transaction,
            "Vita virtual-display mode change");
        return new SessionModeResult(selected.FriendlyName, mode);
    }

    private DisplayDescriptor ActivateWithRetry(
        string? displayMatch,
        int width,
        int height,
        int fps,
        bool forceSdr,
        bool persistMode,
        int activationAttempts,
        bool requireExactVitaTarget = true)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < activationAttempts; attempt++)
        {
            try
            {
                return displays.ActivateVirtualDisplay(
                    displayMatch,
                    width,
                    height,
                    fps,
                    forceSdr,
                    persistMode,
                    requireExactVitaTarget);
            }
            catch (Exception error) when (
                error is InvalidOperationException or Win32Exception)
            {
                lastError = error;
                if (attempt < activationAttempts - 1)
                {
                    Thread.Sleep(500);
                }
            }
        }
        throw new InvalidOperationException(
            $"The virtual display did not become available within {activationAttempts * 0.5:0.#} seconds.",
            lastError);
    }

    private static void RequireFullyReadyBackendLocked(
        DisplayTransactionLease transaction)
    {
        transaction.RequireActive();
        BackendLifecycleStateStore.RequireNoUninstallInProgress();
        if (DeferredHostSetupStore.Load() is not null)
        {
            throw new InvalidOperationException(
                "Vita host setup is still being completed after an update. " +
                "Finish Enable Vita host features in the control panel before streaming.");
        }
        var backend = BackendLifecycleManager.Inspect();
        if (backend.DesiredState == BackendDesiredState.Enabled &&
            backend.Status == BackendLifecycleStatus.Enabled)
        {
            return;
        }

        var details = backend.Issues.Count == 0
            ? "The enabled lifecycle could not be verified."
            : string.Join(" ", backend.Issues.Take(2));
        throw new InvalidOperationException(
            "Vita streaming is not fully ready. Open the host control panel " +
            "as Administrator, choose Enable Vita host features, and run " +
            $"Check readiness before reconnecting. {details}");
    }

}
