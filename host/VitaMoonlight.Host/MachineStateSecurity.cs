using Microsoft.Win32;

namespace VitaMoonlight.Host;

internal static class MachineStateSecurity
{
    private const string RegistryPath =
        @"SOFTWARE\VitaMoonlight\Host";
    private const string ProtectionValue =
        "MachineStateProtectionVersion";
    // Version 2 moves trusted state out of replaceable ProgramData children
    // and beneath the protected Program Files installation.
    private const int CurrentProtectionVersion = 2;

    internal static void Secure()
    {
        // The operational entry point removes test-only path overrides before
        // this method runs. Keep the state root fixed even when Secure is
        // invoked directly by an installer or future integration.
        Environment.SetEnvironmentVariable(
            "VITA_MOONLIGHT_STATE_DIR",
            null);
        if (!IsProtectionInitialized())
        {
            throw new InvalidOperationException(
                "Legacy machine state has not been migrated. Run " +
                "`session recover-upgrade` from the installed Administrator " +
                "companion before using protected state.");
        }
        SecureCore();
        InstallResidueCleanup.RunForInstalledPayloadIfNeeded();
    }

    internal static void SecureAfterLegacyMigration()
    {
        SecureCore();
        MarkProtectionInitialized();
        InstallResidueCleanup.RunForInstalledPayloadIfNeeded();
    }

    internal static void SecureWhileDisplayTransactionHeld(
        DisplayTransactionLease transaction)
    {
        transaction.RequireActive();
        Environment.SetEnvironmentVariable(
            "VITA_MOONLIGHT_STATE_DIR",
            null);
        if (!IsProtectionInitialized())
        {
            throw new InvalidOperationException(
                "Legacy machine state has not been migrated. Run " +
                "`session recover-upgrade` from the installed Administrator " +
                "companion before using protected state.");
        }
        SecureCore(skipDisplayTransactionFile: true);
    }

    private static void SecureCore(
        bool skipDisplayTransactionFile = false)
    {
        // Secure the root itself first. No recursive pathname traversal is
        // used: every object is opened with OPEN_REPARSE_POINT, checked by
        // handle, assigned an exact protected DACL, and (for files) rejected
        // when it has another hard-link name.
        SecureContainer();

        foreach (var path in ProtectedMachineFiles())
        {
            if (skipDisplayTransactionFile &&
                string.Equals(
                    path,
                    HostStatePaths.LockFile,
                    StringComparison.OrdinalIgnoreCase))
            {
                // The active typed lease already proves this exact protected
                // file is open exclusively. Reopening it here would deadlock
                // the caller against its own FileShare.None handle.
                continue;
            }
            TrustedFileSystem.SecureExistingFile(path);
        }
        foreach (var path in WritableDiagnosticFiles())
        {
            TrustedFileSystem.SecureExistingFile(path);
        }
    }

    internal static bool IsProtectionInitialized()
    {
        if (!OperatingSystem.IsWindows()) return true;
        using var localMachine = RegistryKey.OpenBaseKey(
            RegistryHive.LocalMachine,
            RegistryView.Registry64);
        using var key = localMachine.OpenSubKey(RegistryPath);
        return key?.GetValue(ProtectionValue) is int version &&
               version >= CurrentProtectionVersion;
    }

    private static void MarkProtectionInitialized()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var localMachine = RegistryKey.OpenBaseKey(
            RegistryHive.LocalMachine,
            RegistryView.Registry64);
        using var key = localMachine.CreateSubKey(
            RegistryPath,
            writable: true)
            ?? throw new UnauthorizedAccessException(
                "Windows could not record protected machine-state migration.");
        key.SetValue(
            ProtectionValue,
            CurrentProtectionVersion,
            RegistryValueKind.DWord);
    }

    internal static void SecureContainer()
    {
        TrustedFileSystem.SecureDirectory(
            HostStatePaths.Root);
        TrustedFileSystem.SecureDirectory(
            HostStatePaths.DiagnosticsDirectory);
    }

    internal static void SecureDiagnostics()
    {
        SecureContainer();
        foreach (var path in WritableDiagnosticFiles())
        {
            TrustedFileSystem.SecureExistingFile(path);
        }
    }

    /// <summary>
    /// Fast-path precondition for the high-frequency authenticated stream
    /// heartbeat. The agent performs the full ACL walk once at startup; each
    /// exact journal open still rejects reparse points/hard links and applies
    /// the protected file ACL, without rescanning unrelated machine state.
    /// </summary>
    internal static void RequireStreamBoundaryLeaseAccess()
    {
        Environment.SetEnvironmentVariable(
            "VITA_MOONLIGHT_STATE_DIR",
            null);
        if (!IsProtectionInitialized())
        {
            throw new InvalidOperationException(
                "Legacy machine state has not been migrated. Run " +
                "`session recover-upgrade` before using stream-boundary state.");
        }
    }

    private static IEnumerable<string> ProtectedMachineFiles()
    {
        yield return HostStatePaths.RecoveryFile;
        yield return HostStatePaths.SettingsFile;
        yield return HostStatePaths.LockFile;
        // The suspend intent is secured only while its dedicated protected
        // cross-process gate is held. Including it in this bulk inventory
        // would let an unrelated state-secure pass race its durable power
        // event publication and turn a safe suspend into a sharing failure.
        yield return HostStatePaths.LastErrorFile;
        yield return DriverNativeModeVerification.VerificationFile;
        yield return DriverConfigurationDirectoryTrust.IdentityFile;
        yield return ManagedVddOwnershipJournal.JournalFile;
        yield return StreamBoundaryLeaseJournal.StateFile;
        yield return StreamBoundaryLeaseJournal.BackupFile;
        yield return StreamBoundaryLeaseJournal.LockFile;
        yield return BackendLifecycleStateStore.StateFile;
        yield return BackendLifecycleStateStore.BackupFile;
        yield return BackendLifecycleStateStore.DisabledIntentFile;
        yield return BackendLifecycleStateStore.UninstallIntentFile;
        yield return DeferredHostSetupStore.PlanFile;
        yield return InstallerMaintenanceFence.StateFile;
        yield return InstallerMaintenanceFence.BackupFile;
    }

    private static IEnumerable<string> WritableDiagnosticFiles()
    {
        yield return HostStatePaths.RescueStatusFile;
        yield return HostStatePaths.RescueLogFile;
    }
}
