using System.Diagnostics;
using System.Security.Cryptography;
using System.Xml.Linq;
using Microsoft.Win32;

namespace VitaMoonlight.Host;

internal sealed class DisplayWizardAdapter
{
    private const string DriverHardwareId = @"ROOT\MttVDD";
    private const string DriverClassGuid = "4D36E968-E325-11CE-BFC1-08002BE10318";
    private const string DriverConfigurationDirectory = @"C:\VirtualDisplayDriver";
    private static readonly IReadOnlyDictionary<string, string> DriverFileHashes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["mttvdd.cat"] = "08A0093FC9B2E32B287A6F8A77CA4DE0A31830D29FC33D2B13A918DC859468F6",
            ["MttVDD.dll"] = "C9CA837F57A98FBD43BC416A7F535A95843626E7759EAF85CF0CD7CE334DBB05",
            ["MttVDD.inf"] = "550D211FE481E74DFE3F9D724ED78BE48B3A9113405965D683D9373E8D672F5D",
            ["vdd_settings.xml"] = "EDB2501D6D5DA17F66D15D4B97A6F4A3F0D8963165AC4A6A6259D95118288020",
            ["nefconw.exe"] = "6B5EE1E9EBF78A921A3E5FB3AAFE137FB1AAAF24A03911A122DDDF88DE6FF932",
        };
    private static readonly string[] BundleAnchorNames =
    {
        "nefconw.exe",
        "PRPlanIT.com-VirtualDisplayDrv_Wiz.exe",
        "VirtualDisplayDrv.exe",
    };
    private static readonly (int Width, int Height)[] VitaResolutions =
    {
        (960, 540),
        (960, 544),
        (1280, 720),
    };
    private const int VitaDisplayRefreshRate = 60;

    private readonly string executablePath;

    private DisplayWizardAdapter(string executablePath)
    {
        this.executablePath = executablePath;
    }

    internal string ExecutablePath => executablePath;

    internal static DisplayWizardAdapter Locate(string? configuredPath)
    {
        var candidates = new List<string?>
        {
            configuredPath,
            Environment.GetEnvironmentVariable("DISPLAYWIZARD_PATH"),
        };
        foreach (var name in BundleAnchorNames)
        {
            candidates.Add(Path.Combine(AppContext.BaseDirectory, "tools", "DisplayWizard", name));
            candidates.Add(Path.Combine(@"C:\IddSampleDriver", name));
        }

        var selected = candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .SelectMany(candidate => Directory.Exists(candidate)
                ? BundleAnchorNames.Select(name => Path.Combine(candidate!, name))
                : new[] { candidate! })
            .FirstOrDefault(File.Exists);
        if (selected is null)
        {
            throw new FileNotFoundException(
                "The signed virtual display driver bundle was not found. Reinstall the host package or pass `--driver-bundle <path>`."
            );
        }
        return new DisplayWizardAdapter(Path.GetFullPath(selected));
    }

    internal void PrepareMode(int width, int height, int fps)
    {
        ValidateDimension(width, nameof(width), 64, 7680);
        ValidateDimension(height, nameof(height), 64, 4320);
        ValidateDimension(fps, nameof(fps), 24, 240);
        ValidateDriverBundle();
        var configurationPath = EnsureDriverConfiguration();
        AddMode(configurationPath, width, height, fps);
        ReloadDriver();
    }

    internal void InstallDriver()
    {
        ValidateDriverBundle();
        EnsureVitaCompatibilityModes();
        var workingDirectory = Path.GetDirectoryName(executablePath)!;
        var driverAlreadyInstalled = IsDriverInstalled();
        if (!driverAlreadyInstalled)
        {
            RunProcess(
                Path.Combine(workingDirectory, "nefconw.exe"),
                workingDirectory,
                30000,
                "--create-device-node",
                "--hardware-id", DriverHardwareId,
                "--class-name", "Display",
                "--class-guid", DriverClassGuid);
            RunProcess(
                "pnputil.exe",
                workingDirectory,
                60000,
                "/add-driver", Path.Combine(workingDirectory, "MttVDD.inf"), "/install");
        }
        ReloadDriver();
    }

    internal bool EnsureVitaCompatibilityModes()
    {
        ValidateDriverBundle();
        var configurationPath = EnsureDriverConfiguration();
        var original = File.ReadAllText(configurationPath);
        var updated = AddVitaCompatibilityModesToConfiguration(original);
        if (string.Equals(original, updated, StringComparison.Ordinal)) return false;
        DisplayTopologyService.AtomicWrite(configurationPath, updated);
        return true;
    }

    internal static bool HasVitaCompatibilityModes()
    {
        var path = Path.Combine(DriverConfigurationDirectory, "vdd_settings.xml");
        if (!File.Exists(path)) return false;
        try
        {
            return HasVitaCompatibilityModesInConfiguration(File.ReadAllText(path));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return false;
        }
    }

    internal void ReloadDriver()
    {
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
            RunProcess(
                "pnputil.exe",
                Path.GetDirectoryName(executablePath)!,
                45000,
                "/restart-device", instanceId);
        }
    }

    internal static bool IsDriverInstalled()
    {
        return IsHardwareIdInstalled(DriverHardwareId);
    }

    internal static bool IsLegacyDriverInstalled() => IsHardwareIdInstalled(@"ROOT\IddSampleDriver");

    internal void ValidateDriverBundle()
    {
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
            var actualHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
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

    private static IReadOnlyList<string> FindDriverInstanceIds()
    {
        var instanceIds = new List<string>();
        if (!OperatingSystem.IsWindows()) return instanceIds;
        using var displayDevices = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\ROOT\DISPLAY");
        if (displayDevices is null) return instanceIds;
        foreach (var instanceName in displayDevices.GetSubKeyNames())
        {
            using var instance = displayDevices.OpenSubKey(instanceName);
            var hardwareIds = instance?.GetValue("HardwareID") as string[];
            if (hardwareIds?.Any(value => value.Equals(DriverHardwareId, StringComparison.OrdinalIgnoreCase)) == true)
            {
                instanceIds.Add($@"ROOT\DISPLAY\{instanceName}");
            }
        }
        return instanceIds;
    }

    private string EnsureDriverConfiguration()
    {
        var targetPath = Path.Combine(DriverConfigurationDirectory, "vdd_settings.xml");
        if (File.Exists(targetPath)) return targetPath;

        Directory.CreateDirectory(DriverConfigurationDirectory);
        var templatePath = Path.Combine(Path.GetDirectoryName(executablePath)!, "vdd_settings.xml");
        DisplayTopologyService.AtomicWrite(targetPath, File.ReadAllText(templatePath));
        return targetPath;
    }

    private static void AddMode(string configurationPath, int width, int height, int fps)
    {
        var updated = AddModeToConfiguration(File.ReadAllText(configurationPath), width, height, fps);
        DisplayTopologyService.AtomicWrite(configurationPath, updated);
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
                new XElement("height", height),
                new XElement("refresh_rate", fps));
            resolutions.AddFirst(resolution);
        }
        else if (!resolution.Elements("refresh_rate").Any(rate => rate.Value == fps.ToString()))
        {
            resolution.Add(new XElement("refresh_rate", fps));
        }

        return document.ToString(SaveOptions.None);
    }

    internal static string AddVitaCompatibilityModesToConfiguration(string configuration)
    {
        var updated = configuration;
        foreach (var (width, height) in VitaResolutions)
        {
            updated = AddModeToConfiguration(updated, width, height, VitaDisplayRefreshRate);
        }
        return updated;
    }

    internal static bool HasVitaCompatibilityModesInConfiguration(string configuration)
    {
        var document = XDocument.Parse(configuration);
        var resolutions = document.Root?.Element("resolutions")?.Elements("resolution").ToArray();
        if (resolutions is null) return false;
        return VitaResolutions.All(mode => resolutions.Any(resolution =>
            resolution.Element("width")?.Value == mode.Width.ToString() &&
            resolution.Element("height")?.Value == mode.Height.ToString() &&
            resolution.Elements("refresh_rate")
                .Any(rate => rate.Value == VitaDisplayRefreshRate.ToString())));
    }

    private static void RunProcess(string fileName, string workingDirectory, int timeoutMilliseconds, params string[] arguments)
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
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{Path.GetFileName(fileName)} exited with code {process.ExitCode}. {error} {output}".Trim());
        }
    }

    private static void ValidateDimension(int value, string name, int minimum, int maximum)
    {
        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(name, $"Value must be between {minimum} and {maximum}.");
        }
    }
}
