using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Xml.Linq;
using Microsoft.Win32;

namespace VitaMoonlight.Host;

internal enum PnPUtilExitDisposition
{
    Success,
    ContinueToVerification,
    RestartRequired,
    Failure,
}

internal sealed record ManagedVddDeviceStatus(
    string InstanceId,
    bool Present,
    bool Enabled,
    uint DeviceStatus,
    uint ProblemCode);

internal sealed class HostRestartRequiredException(string message) : Exception(message);

internal sealed class DisplayWizardAdapter
{
    private const string DriverHardwareId = @"ROOT\MttVDD";
    private const string DriverClassGuid = "4D36E968-E325-11CE-BFC1-08002BE10318";
    private const string DriverConfigurationDirectory = @"C:\VirtualDisplayDriver";
    private const uint CrSuccess = 0;
    private const uint CrNoSuchDevNode = 0x0000000d;
    private const uint CmDisableUiNotOk = 0x00000004;
    private const uint CmDisablePersist = 0x00000008;
    private const uint CmProblemDisabled = 22;
    private const uint DeviceNodeStarted = 0x00000008;
    private static readonly IReadOnlyDictionary<string, string> DriverFileHashes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["mttvdd.cat"] = "08A0093FC9B2E32B287A6F8A77CA4DE0A31830D29FC33D2B13A918DC859468F6",
            ["MttVDD.dll"] = "C9CA837F57A98FBD43BC416A7F535A95843626E7759EAF85CF0CD7CE334DBB05",
            ["MttVDD.inf"] = "550D211FE481E74DFE3F9D724ED78BE48B3A9113405965D683D9373E8D672F5D",
            ["vdd_settings.xml"] = "EDB2501D6D5DA17F66D15D4B97A6F4A3F0D8963165AC4A6A6259D95118288020",
            ["nefconw.exe"] = "6B5EE1E9EBF78A921A3E5FB3AAFE137FB1AAAF24A03911A122DDDF88DE6FF932",
        };
    private readonly string executablePath;

    private DisplayWizardAdapter(string executablePath)
    {
        this.executablePath = executablePath;
    }

    internal string ExecutablePath => executablePath;
    internal static string DriverConfigurationDirectoryPath =>
        DriverConfigurationDirectory;
    internal static string DriverConfigurationPath =>
        Path.Combine(DriverConfigurationDirectory, "vdd_settings.xml");

    internal static DisplayWizardAdapter LocateBundled()
    {
        var selected = Path.Combine(
            AppContext.BaseDirectory,
            "tools",
            "DisplayWizard",
            "nefconw.exe");
        if (!File.Exists(selected))
        {
            throw new FileNotFoundException(
                "The signed virtual display driver bundle was not found. " +
                "Repair the installed host or re-extract the complete portable package."
            );
        }
        return new DisplayWizardAdapter(Path.GetFullPath(selected));
    }

    internal static DisplayWizardAdapter LocateBundledForUninstall()
    {
        var directory = SunshineOwnershipJournal.ValidateConfigurationDirectory(
            Path.Combine(AppContext.BaseDirectory, "tools", "DisplayWizard"));
        var selected = Path.Combine(directory, "nefconw.exe");
        if (!File.Exists(selected))
        {
            throw new FileNotFoundException(
                "The installed virtual display driver bundle was not found. " +
                "Repair Vita Moonlight Host before removing its VDD.");
        }
        SunshineOwnershipJournal.ValidateOwnedFile(selected);
        return new DisplayWizardAdapter(Path.GetFullPath(selected));
    }

    internal void PrepareMode(
        DisplayTransactionLease transaction,
        int width,
        int height,
        int fps)
    {
        transaction.RequireActive();
        RequireProtectedBundle();
        ValidateDimension(width, nameof(width), 64, 7680);
        ValidateDimension(height, nameof(height), 64, 4320);
        ValidateDimension(fps, nameof(fps), 24, 240);
        ValidateDriverBundle();
        var configurationPath = EnsureDriverConfiguration();
        AddMode(configurationPath, width, height, fps);
        ReloadDriver(transaction);
    }

    internal void InstallDriver(DisplayTransactionLease transaction)
    {
        transaction.RequireActive();
        RequireProtectedBundle();
        ValidateDriverBundle();
        PrepareDriverConfigurationDirectoryForInstall(transaction);
        EnsureDriverConfiguration();
        DriverNativeModeVerification.Invalidate();
        var workingDirectory = Path.GetDirectoryName(executablePath)!;
        var driverAlreadyInstalled = IsDriverInstalled();
        if (!driverAlreadyInstalled)
        {
            EnsureProcessSucceeded(RunProcess(
                Path.Combine(workingDirectory, "nefconw.exe"),
                workingDirectory,
                30000,
                "--create-device-node",
                "--hardware-id", DriverHardwareId,
                "--class-name", "Display",
                "--class-guid", DriverClassGuid));
        }

        // Always stage and install the pinned package. This repairs damaged
        // files, updates an older package, and rebinds an existing root device
        // instead of treating the mere presence of its hardware ID as healthy.
        EnsurePnPUtilSucceeded(RunProcess(
            "pnputil.exe",
            workingDirectory,
            60000,
            "/add-driver", Path.Combine(workingDirectory, "MttVDD.inf"), "/install"));

        // A third-party package action must not silently replace the fixed
        // directory we pinned before invoking it. Explicit install/repair may
        // safely detach such a replacement and create a fresh protected
        // directory; normal reload and runtime operations only verify.
        PrepareDriverConfigurationDirectoryForInstall(transaction);

        // Apply the managed modes after staging/installing the package. A real
        // upgrade can replace C:\VirtualDisplayDriver\vdd_settings.xml with
        // the driver's stock copy, while a repair of an equal/newer package
        // leaves the existing file in place. Normalizing at this point handles
        // both cases without assuming a clean installation.
        EnsureVitaCompatibilityModes(transaction);
        ReloadDriver(transaction);
    }

    internal bool UninstallDriver(DisplayTransactionLease transaction)
    {
        transaction.RequireActive();
        RequireProtectedBundle();
        var topology = new DisplayTopologyService();
        topology.RecoverPhysicalDisplays();
        topology.DisableManagedVirtualDisplays();
        UninstallManager.VerifyPhysicalOnlyTopology(topology);

        if (!IsDriverInstalled() &&
            FindDriverPackageNames().Count == 0)
        {
            // Nothing can consume the fixed path. Remove it only if it is the
            // exact directory recorded by this installation; an unknown entry
            // is unrelated data and is deliberately left alone.
            if (DriverConfigurationDirectoryTrust.TryAcquireVerified(
                    DriverConfigurationDirectory,
                    out var trustedDirectory,
                    out _))
            {
                using (trustedDirectory)
                {
                    TrustedFileSystem.DeleteFile(
                        DriverConfigurationPath);
                }
                DriverConfigurationDirectoryTrust
                    .DeleteTrustedDirectoryIfEmpty(
                        DriverConfigurationDirectory);
            }
            DriverNativeModeVerification.Invalidate();
            return false;
        }

        using (AcquireDriverConfigurationDirectory())
        {
            // Refuse to remove a driver whose fixed configuration path is no
            // longer the protected directory recorded by this installation.
        }

        var serviceName = StreamingHostLocator.FindSunshineServiceName();
        var restartSunshine =
            WindowsServiceManager.GetState(serviceName) == WindowsServiceState.Running;
        Exception? operationError = null;
        if (restartSunshine)
        {
            WindowsServiceManager.Stop(serviceName, "Sunshine");
        }

        try
        {
            var installations = FindDriverInstallations();
            if (installations.Any(installation =>
                    string.IsNullOrWhiteSpace(installation.InfPath)))
            {
                throw new InvalidOperationException(
                    "Windows found the MTT virtual display device but did not expose its driver-store package. " +
                    "The driver was left installed; remove it from Device Manager instead.");
            }
            var driverPackages = FindDriverPackageNames()
                .Concat(installations
                    .Select(installation => installation.InfPath)
                    .Where(infPath => !string.IsNullOrWhiteSpace(infPath))
                    .Select(infPath => infPath!))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var restartRequired = false;
            var pendingDeviceRemovals = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            var pendingPackageRemovals = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var installation in installations)
            {
                var pendingRestart =
                    EnsurePnPUtilRemovalAccepted(RunProcess(
                    "pnputil.exe",
                    Path.GetDirectoryName(executablePath)!,
                    60000,
                    "/remove-device", installation.InstanceId));
                restartRequired |= pendingRestart;
                if (pendingRestart)
                {
                    pendingDeviceRemovals.Add(
                        installation.InstanceId);
                }
            }

            foreach (var infPath in driverPackages)
            {
                var pendingRestart =
                    EnsurePnPUtilRemovalAccepted(RunProcess(
                    "pnputil.exe",
                    Path.GetDirectoryName(executablePath)!,
                    60000,
                    "/delete-driver", infPath,
                    "/uninstall",
                    "/force"));
                restartRequired |= pendingRestart;
                if (pendingRestart)
                {
                    pendingPackageRemovals.Add(infPath);
                }
            }

            IReadOnlyList<DriverInstallation> remainingInstallations = [];
            IReadOnlyList<string> remainingPackages = [];
            for (var attempt = 0; attempt < 20; attempt++)
            {
                remainingInstallations = FindDriverInstallations();
                remainingPackages = FindDriverPackageNames();
                var unexpectedDevice =
                    remainingInstallations.Any(installation =>
                        !pendingDeviceRemovals.Contains(
                            installation.InstanceId));
                var unexpectedPackage =
                    remainingPackages.Any(package =>
                        !pendingPackageRemovals.Contains(package));
                if (!unexpectedDevice &&
                    !unexpectedPackage)
                {
                    break;
                }
                Thread.Sleep(250);
            }
            remainingInstallations = FindDriverInstallations();
            remainingPackages = FindDriverPackageNames();
            var unexpectedDevices = remainingInstallations
                .Where(installation =>
                    !pendingDeviceRemovals.Contains(
                        installation.InstanceId))
                .Select(installation => installation.InstanceId)
                .ToArray();
            var unexpectedPackages = remainingPackages
                .Where(package =>
                    !pendingPackageRemovals.Contains(package))
                .ToArray();
            if (unexpectedDevices.Length > 0 ||
                (IsDriverInstalled() &&
                 remainingInstallations.Count == 0))
            {
                throw new InvalidOperationException(
                    "Windows retained an MTT virtual display device that was " +
                    "not reported as pending restart.");
            }
            if (unexpectedPackages.Length > 0)
            {
                throw new InvalidOperationException(
                    "Windows retained an MttVDD driver-store package that was " +
                    "not reported as pending restart.");
            }

            DriverNativeModeVerification.Invalidate();
            if (restartRequired)
            {
                topology.RecoverPhysicalDisplays();
                UninstallManager.VerifyPhysicalOnlyTopology(topology);
                return true;
            }
            using (AcquireDriverConfigurationDirectory())
            {
                TrustedFileSystem.DeleteFile(DriverConfigurationPath);
            }
            DriverConfigurationDirectoryTrust.DeleteTrustedDirectoryIfEmpty(
                DriverConfigurationDirectory);

            topology.RecoverPhysicalDisplays();
            UninstallManager.VerifyPhysicalOnlyTopology(topology);
            return restartRequired;
        }
        catch (Exception error)
        {
            operationError = error;
            throw;
        }
        finally
        {
            if (restartSunshine)
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
                            "The virtual display was removed, but Sunshine could not be restarted. " +
                            restartError.Message,
                            restartError);
                    }
                    throw new AggregateException(
                        "Virtual-display removal failed and Sunshine could not be restarted.",
                        operationError,
                        restartError);
                }
            }
        }
    }

    internal bool EnsureVitaCompatibilityModes(
        DisplayTransactionLease transaction)
    {
        transaction.RequireActive();
        RequireProtectedBundle();
        ValidateDriverBundle();
        using var directoryLease = AcquireDriverConfigurationDirectory();
        var configurationPath = EnsureDriverConfigurationUnderLease();
        var original = TrustedFileSystem.ReadAllText(configurationPath);
        var updated = AddVitaCompatibilityModesToConfiguration(original);
        if (string.Equals(original, updated, StringComparison.Ordinal)) return false;
        DriverNativeModeVerification.Invalidate();
        TrustedFileSystem.WriteAllText(configurationPath, updated);
        return true;
    }

    internal static bool HasVitaCompatibilityModes()
    {
        var path = DriverConfigurationPath;
        if (!DriverConfigurationDirectoryTrust.TryAcquireVerified(
                DriverConfigurationDirectory,
                out var directoryLease,
                out _))
        {
            return false;
        }
        using (directoryLease)
            try
            {
                if (!File.Exists(path)) return false;
                return HasVitaCompatibilityModesInConfiguration(
                    TrustedFileSystem.ReadAllText(path));
            }
            catch (Exception error) when (
                error is IOException or
                    UnauthorizedAccessException or
                    InvalidDataException or
                    System.ComponentModel.Win32Exception or
                    System.Xml.XmlException)
            {
                return false;
            }
    }

    internal void ReloadDriver(DisplayTransactionLease transaction)
    {
        transaction.RequireActive();
        RequireProtectedBundle();
        EnsureVitaCompatibilityModes(transaction);
        // Hold a read-only directory lease that denies write/delete sharing
        // for the entire device restart. The fixed name must still map to the
        // file identity born with our protected DACL before SYSTEM consumes
        // its configuration.
        using var directoryLease = AcquireDriverConfigurationDirectory();
        if (!IsDriverInstalled())
        {
            throw new InvalidOperationException("The signed virtual display driver is not installed.");
        }
        var instanceIds = FindDriverInstanceIds();
        if (instanceIds.Count == 0)
        {
            throw new InvalidOperationException("The virtual display driver is installed, but its PnP instance could not be resolved.");
        }
        foreach (var instanceId in instanceIds)
        {
            EnsurePnPUtilSucceeded(
                RunProcess(
                    "pnputil.exe",
                    Path.GetDirectoryName(executablePath)!,
                    45000,
                    "/enable-device", instanceId),
                allowAlreadyEnabledNoOp: true);
            EnsurePnPUtilSucceeded(RunProcess(
                "pnputil.exe",
                Path.GetDirectoryName(executablePath)!,
                45000,
                "/restart-device", instanceId));
        }
    }

    /// <summary>
    /// Requests a normal Windows device rescan without disabling any display.
    /// This is the first recovery step for an older installation which left
    /// only the managed VDD in QueryDisplayConfig.
    /// </summary>
    internal static void RescanDisplayDevicesForRecovery()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Display-device recovery is available only on Windows.");
        }
        var systemDirectory = Environment.GetFolderPath(
            Environment.SpecialFolder.System);
        EnsureProcessSucceeded(RunProcess(
            Path.Combine(systemDirectory, "pnputil.exe"),
            systemDirectory,
            45000,
            "/scan-devices"));
    }

    /// <summary>
    /// Restarts one and only one present, enabled MTT VDD instance. The caller
    /// must separately prove that the active topology is the exact managed
    /// VDD-only recovery case and must retain the display transaction through
    /// the final physical-only proof.
    /// </summary>
    internal static string RestartExactManagedVddForRecovery(
        DisplayTransactionLease transaction)
    {
        transaction.RequireActive();
        var instanceId = SelectExactManagedVddRestartTarget(
            InspectManagedDriverDevices());
        var systemDirectory = Environment.GetFolderPath(
            Environment.SpecialFolder.System);
        EnsurePnPUtilSucceeded(RunProcess(
            Path.Combine(systemDirectory, "pnputil.exe"),
            systemDirectory,
            45000,
            "/restart-device",
            instanceId));
        return instanceId;
    }

    internal static string SelectExactManagedVddRestartTarget(
        IEnumerable<ManagedVddDeviceStatus> devices)
    {
        var candidates = devices
            .Where(device => device.Present && device.Enabled)
            .Select(device => device.InstanceId)
            .Where(instanceId => !string.IsNullOrWhiteSpace(instanceId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (candidates.Length != 1)
        {
            throw new InvalidOperationException(
                "Vita Moonlight will not restart a virtual display during recovery because Windows did not expose exactly one present, enabled managed MTT device. " +
                $"Observed managed candidates: {candidates.Length}. No unrelated or ambiguous display device was changed.");
        }
        return candidates[0];
    }

    internal static bool HasSingleManagedVddRestartTargetForTest(
        IEnumerable<ManagedVddDeviceStatus> devices) =>
        devices
            .Where(device => device.Present && device.Enabled)
            .Select(device => device.InstanceId)
            .Where(instanceId => !string.IsNullOrWhiteSpace(instanceId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .Count() == 1;

    internal static IReadOnlyList<ManagedVddDeviceStatus>
        InspectManagedDriverDevices()
    {
        var devices = new List<ManagedVddDeviceStatus>();
        foreach (var installation in FindDriverInstallations())
        {
            var deviceNode = 0u;
            var locateResult = CM_Locate_DevNodeW(
                ref deviceNode,
                installation.InstanceId,
                0);
            if (locateResult == CrNoSuchDevNode)
            {
                devices.Add(new ManagedVddDeviceStatus(
                    installation.InstanceId,
                    false,
                    false,
                    0,
                    0));
                continue;
            }
            EnsureConfigurationManagerSucceeded(
                locateResult,
                $"locate managed virtual display {installation.InstanceId}");
            var statusResult = CM_Get_DevNode_Status(
                out var deviceStatus,
                out var problemCode,
                deviceNode,
                0);
            EnsureConfigurationManagerSucceeded(
                statusResult,
                $"inspect managed virtual display {installation.InstanceId}");
            devices.Add(new ManagedVddDeviceStatus(
                installation.InstanceId,
                true,
                IsManagedDeviceEnabled(deviceStatus, problemCode),
                deviceStatus,
                problemCode));
        }
        return devices;
    }

    internal static void SetManagedDriverEnabled(
        DisplayTransactionLease transaction,
        IEnumerable<string> instanceIds,
        bool enabled)
    {
        transaction.RequireActive();
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Virtual display device control is available only on Windows.");
        }
        if (!enabled)
        {
            // Device disable is permitted only after a separately queried
            // topology proves that at least one physical path is active and
            // the managed virtual path is already inactive.
            UninstallManager.VerifyPhysicalOnlyTopology(
                new DisplayTopologyService());
        }

        var requested = instanceIds
            .Where(instanceId => !string.IsNullOrWhiteSpace(instanceId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (requested.Count == 0) return;

        var devices = InspectManagedDriverDevices();
        var knownInstanceIds = devices
            .Select(device => device.InstanceId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknown = requested
            .Where(instanceId => !knownInstanceIds.Contains(instanceId))
            .ToArray();
        if (enabled && unknown.Length > 0)
        {
            throw new InvalidOperationException(
                "One or more previously enabled managed virtual display instances no longer exist. " +
                "Vita Moonlight did not enable a different/shared instance: " +
                string.Join(", ", unknown));
        }

        foreach (var device in devices.Where(device =>
                     requested.Contains(device.InstanceId) &&
                     device.Present &&
                     device.Enabled != enabled))
        {
            var deviceNode = 0u;
            EnsureConfigurationManagerSucceeded(
                CM_Locate_DevNodeW(
                    ref deviceNode,
                    device.InstanceId,
                    0),
                $"locate managed virtual display {device.InstanceId}");
            var result = enabled
                ? CM_Enable_DevNode(deviceNode, 0)
                : CM_Disable_DevNode(
                    deviceNode,
                    CmDisableUiNotOk | CmDisablePersist);
            EnsureConfigurationManagerSucceeded(
                result,
                $"{(enabled ? "enable" : "disable")} managed virtual display {device.InstanceId}");
        }

        for (var attempt = 0; attempt < 40; attempt++)
        {
            var current = InspectManagedDriverDevices();
            var currentById = current.ToDictionary(
                device => device.InstanceId,
                StringComparer.OrdinalIgnoreCase);
            if (requested.All(instanceId =>
                    currentById.TryGetValue(instanceId, out var device)
                        ? enabled
                            ? device.Present && device.Enabled
                            : !device.Present || !device.Enabled
                        : !enabled))
            {
                return;
            }
            Thread.Sleep(250);
        }
        throw new InvalidOperationException(
            $"Windows did not confirm that every managed virtual display device became {(enabled ? "enabled" : "disabled")} within 10 seconds.");
    }

    internal static bool IsManagedDeviceEnabled(
        uint deviceStatus,
        uint problemCode) =>
        problemCode == 0 &&
        (deviceStatus & DeviceNodeStarted) != 0;

    private static void EnsureConfigurationManagerSucceeded(
        uint result,
        string operation)
    {
        if (result == CrSuccess) return;
        throw new InvalidOperationException(
            $"Windows Configuration Manager could not {operation} (CONFIGRET 0x{result:X8}).");
    }

    internal static bool IsDriverInstalled()
    {
        return IsHardwareIdInstalled(DriverHardwareId);
    }

    internal static bool IsLegacyDriverInstalled() => IsHardwareIdInstalled(@"ROOT\IddSampleDriver");

    internal void ValidateDriverBundle()
    {
        InstallationTrust.RequireBundledReadOnlyFile(
            executablePath,
            Path.Combine("tools", "DisplayWizard", "nefconw.exe"),
            "Virtual display driver validation");
        var workingDirectory = Path.GetDirectoryName(executablePath)!;
        foreach (var expected in DriverFileHashes)
        {
            var path = Path.Combine(workingDirectory, expected.Key);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    $"The signed virtual display driver bundle is missing {expected.Key}. Reinstall the host package.",
                    path);
            }
            var actualHash = Convert.ToHexString(
                SHA256.HashData(TrustedFileSystem.ReadAllBytes(path)));
            if (!actualHash.Equals(expected.Value, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"{expected.Key} failed its integrity check.");
            }
        }
    }

    private static bool IsHardwareIdInstalled(string hardwareId)
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var displayDevices = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\ROOT\DISPLAY");
        if (displayDevices is null) return false;
        foreach (var instanceName in displayDevices.GetSubKeyNames())
        {
            using var instance = displayDevices.OpenSubKey(instanceName);
            var hardwareIds = instance?.GetValue("HardwareID") as string[];
            if (hardwareIds?.Any(value => value.Equals(hardwareId, StringComparison.OrdinalIgnoreCase)) == true)
            {
                return true;
            }
        }
        return false;
    }

    private static IReadOnlyList<string> FindDriverInstanceIds() =>
        FindDriverInstallations()
            .Select(installation => installation.InstanceId)
            .ToArray();

    private static IReadOnlyList<DriverInstallation> FindDriverInstallations()
    {
        var installations = new List<DriverInstallation>();
        if (!OperatingSystem.IsWindows()) return installations;
        using var displayDevices = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\ROOT\DISPLAY");
        if (displayDevices is null) return installations;
        foreach (var instanceName in displayDevices.GetSubKeyNames())
        {
            using var instance = displayDevices.OpenSubKey(instanceName);
            var hardwareIds = instance?.GetValue("HardwareID") as string[];
            if (hardwareIds?.Any(value => value.Equals(DriverHardwareId, StringComparison.OrdinalIgnoreCase)) == true)
            {
                var driverKeyName = instance?.GetValue("Driver") as string;
                string? infPath = null;
                if (!string.IsNullOrWhiteSpace(driverKeyName))
                {
                    using var driverKey = Registry.LocalMachine.OpenSubKey(
                        $@"SYSTEM\CurrentControlSet\Control\Class\{driverKeyName}");
                    infPath = driverKey?.GetValue("InfPath") as string;
                }
                installations.Add(new DriverInstallation(
                    $@"ROOT\DISPLAY\{instanceName}",
                    infPath));
            }
        }
        return installations;
    }

    private static IReadOnlyList<string> FindDriverPackageNames()
    {
        var packages = new List<string>();
        if (!OperatingSystem.IsWindows()) return packages;
        using var driverPackages = Registry.LocalMachine.OpenSubKey(
            @"DRIVERS\DriverDatabase\DriverPackages");
        if (driverPackages is null) return packages;
        foreach (var keyName in driverPackages.GetSubKeyNames())
        {
            using var package = driverPackages.OpenSubKey(keyName);
            if (package is null ||
                !string.Equals(
                    package.GetValue("InfName") as string,
                    "MttVDD.inf",
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    package.GetValue("Provider") as string,
                    "MikeTheTech",
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    package.GetValue("Catalog") as string,
                    "MttVDD.cat",
                    StringComparison.OrdinalIgnoreCase) ||
                package.OpenSubKey(@"Descriptors\Root\MttVDD") is not { } descriptor)
            {
                continue;
            }
            descriptor.Dispose();
            var publishedName = package.GetValue(null) as string;
            if (publishedName is not null &&
                System.Text.RegularExpressions.Regex.IsMatch(
                    publishedName,
                    @"\Aoem[0-9]+\.inf\z",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            {
                packages.Add(publishedName);
            }
        }
        return packages;
    }

    private string EnsureDriverConfiguration()
    {
        using var directoryLease = AcquireDriverConfigurationDirectory();
        return EnsureDriverConfigurationUnderLease();
    }

    private string EnsureDriverConfigurationUnderLease()
    {
        var targetPath = DriverConfigurationPath;
        if (File.Exists(targetPath))
        {
            TrustedFileSystem.SecureExistingFile(targetPath);
            return targetPath;
        }

        var templatePath = Path.Combine(Path.GetDirectoryName(executablePath)!, "vdd_settings.xml");
        TrustedFileSystem.WriteAllText(
            targetPath,
            TrustedFileSystem.ReadAllText(templatePath));
        return targetPath;
    }

    private static void AddMode(string configurationPath, int width, int height, int fps)
    {
        using var directoryLease = AcquireDriverConfigurationDirectory();
        var original = TrustedFileSystem.ReadAllText(configurationPath);
        var updated = AddModeToConfiguration(original, width, height, fps);
        if (string.Equals(original, updated, StringComparison.Ordinal)) return;
        DriverNativeModeVerification.Invalidate();
        TrustedFileSystem.WriteAllText(configurationPath, updated);
    }

    private void RequireProtectedBundle()
    {
        InstallationTrust.RequireBundledFile(
            executablePath,
            Path.Combine("tools", "DisplayWizard", "nefconw.exe"),
            "Virtual display driver operation");
    }

    private static void PrepareDriverConfigurationDirectoryForInstall(
        DisplayTransactionLease transaction)
    {
        InstallationTrust.RequireInstalledPayload(
            "Virtual display driver configuration");
        var preparation =
            DriverConfigurationDirectoryTrust.PrepareForInstallOrRepair(
                DriverConfigurationDirectory,
                transaction);
        using (preparation.Lease)
        {
            if (!preparation.Recreated) return;

            DriverNativeModeVerification.Invalidate();
            Console.WriteLine(
                "Created a new protected virtual-display configuration " +
                "directory and pinned its Windows file identity.");
            if (preparation.RetainedQuarantinePath is not null)
            {
                Console.WriteLine(
                    "The previous fixed-path entry was detached without " +
                    "reading its contents and retained at " +
                    $"{preparation.RetainedQuarantinePath}. Windows could " +
                    "not remove it because it was not empty.");
            }
        }
    }

    private static TrustedDirectoryLease
        AcquireDriverConfigurationDirectory()
    {
        InstallationTrust.RequireInstalledPayload(
            "Virtual display driver configuration");
        return DriverConfigurationDirectoryTrust.AcquireVerified(
            DriverConfigurationDirectory);
    }

    internal static string AddModeToConfiguration(string configuration, int width, int height, int fps)
    {
        var document = XDocument.Parse(configuration, LoadOptions.PreserveWhitespace);
        var root = document.Root ?? throw new InvalidDataException("The virtual display configuration has no root element.");
        var resolutions = root.Element("resolutions");
        if (resolutions is null)
        {
            resolutions = new XElement("resolutions");
            root.Add(resolutions);
        }

        var resolution = resolutions.Elements("resolution").FirstOrDefault(candidate =>
            candidate.Element("width")?.Value == width.ToString() &&
            candidate.Element("height")?.Value == height.ToString());
        if (resolution is null)
        {
            resolution = new XElement(
                "resolution",
                new XElement("width", width),
                new XElement("height", height));
            resolutions.AddFirst(resolution);
        }

        var refreshRate = fps.ToString();
        var globalRefreshRates = ReadGlobalRefreshRates(root);
        if (!globalRefreshRates.Contains(refreshRate) &&
            !resolution.Elements("refresh_rate").Any(rate => NormalizeRefreshRate(rate.Value) == refreshRate))
        {
            resolution.Add(new XElement("refresh_rate", fps));
        }

        NormalizeRefreshRates(root);
        return document.ToString(SaveOptions.None);
    }

    internal static string AddVitaCompatibilityModesToConfiguration(string configuration)
    {
        var updated = configuration;
        foreach (var mode in VitaDisplayModes.Supported)
        {
            updated = AddModeToConfiguration(updated, mode.Width, mode.Height, mode.Fps);
        }

        // Advertise the Vita's native mode first while retaining the stock
        // modes for local recovery and compatibility. Setup also activates and
        // verifies this mode once because Windows can retain an older mode for
        // an already-enumerated display.
        var document = XDocument.Parse(updated, LoadOptions.PreserveWhitespace);
        var root = document.Root
            ?? throw new InvalidDataException("The virtual display configuration has no root element.");
        NormalizeRefreshRates(root);
        var resolutions = root.Element("resolutions")
            ?? throw new InvalidDataException("The virtual display configuration has no resolutions element.");
        var currentModes = resolutions.Elements("resolution").ToArray();
        var preferredModes = VitaDisplayModes.Supported
            .Select(mode => currentModes.First(resolution =>
                resolution.Element("width")?.Value == mode.Width.ToString() &&
                resolution.Element("height")?.Value == mode.Height.ToString()))
            .ToArray();
        if (currentModes.Take(preferredModes.Length).SequenceEqual(preferredModes))
        {
            return updated;
        }

        for (var index = preferredModes.Length - 1; index >= 0; index--)
        {
            preferredModes[index].Remove();
            resolutions.AddFirst(preferredModes[index]);
        }
        return document.ToString(SaveOptions.None);
    }

    internal static bool HasVitaCompatibilityModesInConfiguration(string configuration)
    {
        var document = XDocument.Parse(configuration);
        var root = document.Root;
        var resolutions = root?.Element("resolutions")?.Elements("resolution").ToArray();
        if (resolutions is null) return false;
        var globalRefreshRates = ReadGlobalRefreshRates(root!);
        var preferred = resolutions.FirstOrDefault();
        return preferred?.Element("width")?.Value == VitaDisplayModes.Native.Width.ToString() &&
               preferred.Element("height")?.Value == VitaDisplayModes.Native.Height.ToString() &&
               HasEffectiveRefreshRate(preferred, VitaDisplayModes.Native.Fps, globalRefreshRates) &&
               VitaDisplayModes.Supported.All(mode => resolutions.Any(resolution =>
            resolution.Element("width")?.Value == mode.Width.ToString() &&
            resolution.Element("height")?.Value == mode.Height.ToString() &&
            HasEffectiveRefreshRate(resolution, mode.Fps, globalRefreshRates)));
    }

    private static HashSet<string> ReadGlobalRefreshRates(XElement root) =>
        root.Element("global")?
            .Elements("g_refresh_rate")
            .Select(rate => NormalizeRefreshRate(rate.Value))
            .Where(rate => rate.Length > 0)
            .ToHashSet(StringComparer.Ordinal) ??
        new HashSet<string>(StringComparer.Ordinal);

    private static bool HasEffectiveRefreshRate(
        XElement resolution,
        int fps,
        IReadOnlySet<string> globalRefreshRates)
    {
        var refreshRate = fps.ToString();
        return globalRefreshRates.Contains(refreshRate) ||
               resolution.Elements("refresh_rate")
                   .Any(rate => NormalizeRefreshRate(rate.Value) == refreshRate);
    }

    private static void NormalizeRefreshRates(XElement root)
    {
        var global = root.Element("global");
        var globalRefreshRates = new HashSet<string>(StringComparer.Ordinal);
        if (global is not null)
        {
            foreach (var rate in global.Elements("g_refresh_rate").ToArray())
            {
                var normalized = NormalizeRefreshRate(rate.Value);
                if (normalized.Length == 0 || !globalRefreshRates.Add(normalized))
                {
                    rate.Remove();
                }
                else
                {
                    rate.Value = normalized;
                }
            }
        }

        var resolutions = root.Element("resolutions");
        if (resolutions is null) return;
        foreach (var resolution in resolutions.Elements("resolution"))
        {
            var localRefreshRates = new HashSet<string>(StringComparer.Ordinal);
            foreach (var rate in resolution.Elements("refresh_rate").ToArray())
            {
                var normalized = NormalizeRefreshRate(rate.Value);
                if (normalized.Length == 0 ||
                    globalRefreshRates.Contains(normalized) ||
                    !localRefreshRates.Add(normalized))
                {
                    rate.Remove();
                }
                else
                {
                    rate.Value = normalized;
                }
            }
        }
    }

    private static string NormalizeRefreshRate(string value) =>
        decimal.TryParse(
            value.Trim(),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var parsedRate) &&
        parsedRate > 0
            ? parsedRate.ToString("0.################", CultureInfo.InvariantCulture)
            : value.Trim();

    internal static PnPUtilExitDisposition ClassifyPnPUtilExitCode(
        int exitCode,
        bool allowAlreadyEnabledNoOp = false) =>
        exitCode switch
        {
            0 => PnPUtilExitDisposition.Success,
            259 => PnPUtilExitDisposition.ContinueToVerification,
            50 when allowAlreadyEnabledNoOp => PnPUtilExitDisposition.ContinueToVerification,
            1641 or 3010 => PnPUtilExitDisposition.RestartRequired,
            _ => PnPUtilExitDisposition.Failure,
        };

    private static ProcessExecutionResult RunProcess(
        string fileName,
        string workingDirectory,
        int timeoutMilliseconds,
        params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workingDirectory,
            },
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (!process.Start())
        {
            throw new InvalidOperationException($"Could not start {Path.GetFileName(fileName)}.");
        }
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeoutMilliseconds))
        {
            process.Kill(true);
            process.WaitForExit();
            throw new TimeoutException($"{Path.GetFileName(fileName)} timed out while running {string.Join(' ', arguments)}.");
        }
        var output = outputTask.GetAwaiter().GetResult();
        var error = errorTask.GetAwaiter().GetResult();
        return new ProcessExecutionResult(
            Path.GetFileName(fileName),
            process.ExitCode,
            output,
            error);
    }

    private static void EnsureProcessSucceeded(ProcessExecutionResult result)
    {
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{result.FileName} exited with code {result.ExitCode}. {result.Error} {result.Output}".Trim());
        }
    }

    private static void EnsurePnPUtilSucceeded(
        ProcessExecutionResult result,
        bool allowAlreadyEnabledNoOp = false)
    {
        switch (ClassifyPnPUtilExitCode(result.ExitCode, allowAlreadyEnabledNoOp))
        {
            case PnPUtilExitDisposition.Success:
                return;
            case PnPUtilExitDisposition.ContinueToVerification:
                Console.WriteLine(result.ExitCode == 50
                    ? "PnPUtil reports that enabling the already-enabled display device is unsupported on this Windows build. " +
                      "Continuing to device restart and explicit native-mode verification."
                    : "PnPUtil made no device change because no target matched or Windows already has an equal/newer driver. " +
                      "Continuing to explicit display enumeration and native-mode verification.");
                return;
            case PnPUtilExitDisposition.RestartRequired:
                DriverNativeModeVerification.Invalidate();
                throw new HostRestartRequiredException(
                    "Windows accepted the virtual display driver operation and requires a restart before native 960x544 can be verified.");
            default:
                DriverNativeModeVerification.Invalidate();
                throw new InvalidOperationException(
                    $"{result.FileName} exited with code {result.ExitCode}. {result.Error} {result.Output}".Trim());
        }
    }

    private static bool EnsurePnPUtilRemovalAccepted(ProcessExecutionResult result)
    {
        switch (ClassifyPnPUtilExitCode(result.ExitCode))
        {
            case PnPUtilExitDisposition.Success:
            case PnPUtilExitDisposition.ContinueToVerification:
                return false;
            case PnPUtilExitDisposition.RestartRequired:
                return true;
            default:
                throw new InvalidOperationException(
                    $"{result.FileName} exited with code {result.ExitCode}. " +
                    $"{result.Error} {result.Output}".Trim());
        }
    }

    private sealed record ProcessExecutionResult(
        string FileName,
        int ExitCode,
        string Output,
        string Error);

    private sealed record DriverInstallation(
        string InstanceId,
        string? InfPath);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Locate_DevNodeW(
        ref uint deviceInstance,
        string deviceInstanceId,
        uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_DevNode_Status(
        out uint status,
        out uint problemNumber,
        uint deviceInstance,
        uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Enable_DevNode(
        uint deviceInstance,
        uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Disable_DevNode(
        uint deviceInstance,
        uint flags);

    private static void ValidateDimension(int value, string name, int minimum, int maximum)
    {
        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(name, $"Value must be between {minimum} and {maximum}.");
        }
    }
}
