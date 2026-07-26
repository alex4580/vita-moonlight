namespace VitaMoonlight.Host;

internal sealed record SunshineIntegrationCleanupResult(
    string ConfigurationDirectory,
    int RemovedHooks,
    bool RemovedGeneratedApplication,
    bool RemovedNativeDisplaySettings);

internal sealed record UninstallPreparationResult(
    IReadOnlyList<string> PhysicalDisplays,
    bool ClearedSavedTransaction);

internal static class UninstallManager
{
    internal static UninstallPreparationResult Prepare() =>
        RecoverPhysicalAndDiscardPendingTransaction();

    internal static UninstallPreparationResult
        RecoverPhysicalAndDiscardPendingTransaction()
    {
        return WithSunshineStopped(
            RecoverPhysicalAndDiscardPendingTransactionCore,
            "Display recovery");
    }

    internal static UninstallPreparationResult
        RecoverPhysicalAndDiscardPendingTransactionCore()
    {
        var topology = new DisplayTopologyService();
        var physicalDisplays = topology.RecoverPhysicalDisplays();
        topology.DisableManagedVirtualDisplays();
        VerifyPhysicalOnlyTopology(topology);

        // Recovery records from older installs lived in a replaceable
        // ProgramData child. Never inspect, traverse, or delete that untrusted
        // tree. Physical recovery above is authoritative and current versions
        // never read the legacy path.

        // Lock the new Program Files state container, then unlink any record
        // from an interrupted v2 setup without parsing or applying it.
        MachineStateSecurity.SecureContainer();
        var clearedSavedTransaction =
            TrustedFileSystem.DeleteFile(
                HostStatePaths.RecoveryFile);
        if (!MachineStateSecurity.IsProtectionInitialized())
        {
            // Preserve no machine decisions from an incomplete migration.
            // Setup will recreate recommended settings in this protected
            // Program Files state root.
            TrustedFileSystem.DeleteFile(HostStatePaths.SettingsFile);
            TrustedFileSystem.DeleteFile(HostStatePaths.LockFile);
            TrustedFileSystem.DeleteFile(HostStatePaths.LastErrorFile);
            TrustedFileSystem.DeleteFile(
                DriverNativeModeVerification.VerificationFile);
            TrustedFileSystem.DeleteFile(
                DriverConfigurationDirectoryTrust.IdentityFile);
        }
        MachineStateSecurity.SecureAfterLegacyMigration();

        VerifyPhysicalOnlyTopology(topology);
        return new UninstallPreparationResult(
            physicalDisplays,
            clearedSavedTransaction);
    }

    internal static SunshineIntegrationCleanupResult CleanupIntegration() =>
        WithSunshineStopped(
            SunshineConfigurator.RemoveManagedIntegration,
            "Sunshine integration cleanup");

    internal static void VerifyPhysicalOnlyTopology(DisplayTopologyService topology)
    {
        var displays = topology.ListDisplays();
        var activePhysical = displays.Where(display =>
            display.IsActive &&
            display.IsAvailable &&
            !DisplayTopologyService.IsLikelyVirtualDisplay(display)).ToArray();
        if (activePhysical.Length == 0)
        {
            throw new InvalidOperationException(
                "Windows did not confirm an active physical display. " +
                "Uninstall will keep the host and its recovery safeguards installed.");
        }

        var activeManagedVirtual = displays.Where(display =>
            display.IsActive &&
            DisplayTopologyService.IsManagedVirtualDisplay(display)).ToArray();
        if (activeManagedVirtual.Length > 0)
        {
            throw new InvalidOperationException(
                "Windows still reports the Vita virtual display as active. " +
                "Uninstall will keep the host and its recovery safeguards installed.");
        }
    }

    private static T WithSunshineStopped<T>(
        Func<T> operation,
        string operationName)
    {
        var serviceName = StreamingHostLocator.FindSunshineServiceName();
        var restartSunshine =
            WindowsServiceManager.GetState(serviceName) ==
            WindowsServiceState.Running;
        Exception? operationError = null;

        if (restartSunshine)
        {
            WindowsServiceManager.Stop(serviceName, "Sunshine");
        }

        try
        {
            return operation();
        }
        catch (Exception error)
        {
            operationError = error;
            throw;
        }
        finally
        {
            if (restartSunshine &&
                WindowsServiceManager.GetState(serviceName) !=
                WindowsServiceState.NotInstalled)
            {
                try
                {
                    WindowsServiceManager.Start(serviceName, "Sunshine");
                }
                catch (Exception restartError)
                {
                    if (operationError is null)
                    {
                        throw new InvalidOperationException(
                            $"{operationName} finished, but Sunshine could not be restarted. " +
                            restartError.Message,
                            restartError);
                    }
                    throw new AggregateException(
                        $"{operationName} failed and Sunshine could not be restarted.",
                        operationError,
                        restartError);
                }
            }
        }
    }
}
