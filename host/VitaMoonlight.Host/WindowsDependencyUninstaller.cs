using System.Diagnostics;
using Microsoft.Win32;

namespace VitaMoonlight.Host;

internal enum DependencyUninstallDisposition
{
    Success,
    RestartRequired,
    Failure,
}

internal sealed record DependencyUninstallResult(
    string DisplayName,
    bool WasInstalled,
    bool RestartRequired,
    int AcceptedRegistrationCount = 0,
    int PendingRestartRegistrationCount = 0);

internal static class WindowsDependencyUninstaller
{
    private static readonly IReadOnlyDictionary<string, DependencyProduct> SupportedProducts =
        new Dictionary<string, DependencyProduct>(StringComparer.OrdinalIgnoreCase)
        {
            ["sunshine"] = new("Sunshine", "LizardByte"),
            ["vigembus"] = new(
                "ViGEm Bus Driver",
                "Nefarius Software Solutions e.U."),
        };

    internal static DependencyUninstallResult Uninstall(string productName)
    {
        if (!SupportedProducts.TryGetValue(productName, out var product))
        {
            throw new ArgumentException(
                "Dependency must be `sunshine` or `vigembus`.",
                nameof(productName));
        }

        var productCodes = FindProductCodes(product);
        if (productCodes.Count == 0)
        {
            var unsupportedInstallation =
                FindUnsupportedInstallationEvidence(productName);
            if (unsupportedInstallation is not null)
            {
                throw new InvalidOperationException(
                    $"{product.DisplayName} is present ({unsupportedInstallation}), " +
                    "but Windows has no matching MSI registration owned by " +
                    $"{product.Publisher}. No dependency was removed. Use its " +
                    "original installer or Windows Installed apps entry instead.");
            }
            return new DependencyUninstallResult(
                product.DisplayName,
                false,
                false);
        }

        var restartRequired = false;
        var pendingRestartProducts = new HashSet<Guid>();
        var acceptedProducts = new HashSet<Guid>();
        foreach (var productCode in productCodes)
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.System),
                        "msiexec.exe"),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            process.StartInfo.ArgumentList.Add("/x");
            process.StartInfo.ArgumentList.Add(productCode.ToString("B"));
            process.StartInfo.ArgumentList.Add("/qn");
            process.StartInfo.ArgumentList.Add("/norestart");
            if (!process.Start())
            {
                throw new InvalidOperationException(
                    $"Windows Installer could not start the {product.DisplayName} uninstall.");
            }
            if (!process.WaitForExit(TimeSpan.FromMinutes(15)))
            {
                throw new TimeoutException(
                    $"Windows Installer did not finish removing {product.DisplayName} within 15 minutes. " +
                    "The MSI transaction was left running; Vita Moonlight Host and its safeguards were kept.");
            }

            switch (ClassifyExitCode(process.ExitCode))
            {
                case DependencyUninstallDisposition.Success:
                    acceptedProducts.Add(productCode);
                    break;
                case DependencyUninstallDisposition.RestartRequired:
                    restartRequired = true;
                    pendingRestartProducts.Add(productCode);
                    acceptedProducts.Add(productCode);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Windows Installer could not remove {product.DisplayName} " +
                        $"(code {process.ExitCode}). It had already accepted " +
                        $"{acceptedProducts.Count} of {productCodes.Count} matching " +
                        "registration(s); those completed MSI changes were not rolled back.");
            }
        }

        IReadOnlyList<Guid> remainingProducts = [];
        for (var attempt = 0; attempt < 40; attempt++)
        {
            remainingProducts = FindProductCodes(product);
            if (remainingProducts.All(
                    pendingRestartProducts.Contains))
            {
                break;
            }
            Thread.Sleep(250);
        }
        remainingProducts = FindProductCodes(product);
        var unexpectedProducts = remainingProducts
            .Where(productCode =>
                !pendingRestartProducts.Contains(productCode))
            .ToArray();
        if (unexpectedProducts.Length > 0)
        {
            throw new InvalidOperationException(
                $"Windows Installer exited successfully, but " +
                $"{product.DisplayName} retains a registration that was not " +
                "reported as pending restart. The host was retained so the " +
                "explicit dependency removal can be retried safely.");
        }

        if (!restartRequired)
        {
            VerifyDependencyAbsent(productName, product);
        }
        return new DependencyUninstallResult(
            product.DisplayName,
            true,
            restartRequired,
            acceptedProducts.Count,
            pendingRestartProducts.Count);
    }

    internal static DependencyUninstallDisposition ClassifyExitCode(int exitCode) =>
        exitCode switch
        {
            0 or 1605 or 1614 => DependencyUninstallDisposition.Success,
            1641 or 3010 => DependencyUninstallDisposition.RestartRequired,
            _ => DependencyUninstallDisposition.Failure,
        };

    private static IReadOnlyList<Guid> FindProductCodes(
        DependencyProduct expected)
    {
        var results = new HashSet<Guid>();
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var uninstall = localMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (uninstall is null) continue;
            foreach (var keyName in uninstall.GetSubKeyNames())
            {
                if (!Guid.TryParse(keyName, out var productCode)) continue;
                using var product = uninstall.OpenSubKey(keyName);
                if (product is not null &&
                    string.Equals(
                    product.GetValue("DisplayName") as string,
                    expected.DisplayName,
                    StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(
                        product.GetValue("Publisher") as string,
                        expected.Publisher,
                        StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(productCode);
                }
            }
        }
        return results.ToArray();
    }

    private static string? FindUnsupportedInstallationEvidence(
        string productName)
    {
        if (productName.Equals("sunshine", StringComparison.OrdinalIgnoreCase))
        {
            var path = StreamingHostLocator.FindSunshineExecutable();
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                return $"executable at {path}";
            }
            var serviceName = StreamingHostLocator.FindSunshineServiceName();
            if (WindowsServiceManager.GetState(serviceName) !=
                WindowsServiceState.NotInstalled)
            {
                return $"Windows service {serviceName}";
            }
            return null;
        }

        return WindowsServiceManager.GetState("ViGEmBus") ==
               WindowsServiceState.NotInstalled
            ? null
            : "Windows service ViGEmBus";
    }

    private static void VerifyDependencyAbsent(
        string productName,
        DependencyProduct product)
    {
        string? evidence = null;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            evidence = FindUnsupportedInstallationEvidence(productName);
            if (evidence is null) return;
            Thread.Sleep(250);
        }

        throw new InvalidOperationException(
            $"Windows removed the matching {product.DisplayName} MSI " +
            $"registration, but {evidence} remains. The host and recovery " +
            "safeguards were retained. Restart Windows, confirm whether the " +
            "dependency belongs to another installation, and retry only if " +
            "you still intend to remove it.");
    }

    private sealed record DependencyProduct(
        string DisplayName,
        string Publisher);
}
