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
        EnsureDriverConfiguration();
        var workingDirectory = Path.GetDirectoryName(executablePath)!;
        if (!IsDriverInstalled())
        {
            RunProcess(
                Path.Combine(workingDirectory, "nefconw.exe"),
                workingDirectory,
                30000,
                "--create-device-node",
                "--hardware-id", DriverHardwareId,
                "--class-name", "Display",
                "--class-guid", DriverClassGuid);
        }
        RunProcess(
            "pnputil.exe",
            workingDirectory,
            60000,
            "/add-driver", Path.Combine(workingDirectory, "MttVDD.inf"), "/install");
    }

    internal void ReloadDriver()
    {
        if (!IsDriverInstalled())
        {
            throw new InvalidOperationException("The signed virtual display driver is not installed.");
        }
        RunProcess(
            "pnputil.exe",
            Path.GetDirectoryName(executablePath)!,
            45000,
            "/restart-device", "/deviceid", DriverHardwareId);
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
