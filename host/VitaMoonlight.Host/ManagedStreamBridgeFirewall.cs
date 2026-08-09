using System.Runtime.InteropServices;

namespace VitaMoonlight.Host;

internal static class ManagedStreamBridgeFirewall
{
    internal const string RuleName =
        "Vita Moonlight authenticated stream boundary (managed)";
    private const string RuleDescription =
        "Managed by Vita Moonlight Host; mutually authenticated Vita display handoff only.";
    private const string RuleGrouping = "Vita Moonlight Host";
    private const int TcpProtocol = 6;
    private const int InboundDirection = 1;
    private const int AllowAction = 1;
    private const int AllProfiles = int.MaxValue;
    private const int ErrorFileNotFound = unchecked((int)0x80070002);

    internal static void InstallOrRepair(int port, string executablePath)
    {
        RequirePort(port);
        var normalizedExecutable = Path.GetFullPath(executablePath);
        var (policy, rules) = OpenRules();
        try
        {
            var existing = TryGetRule(rules);
            if (existing is not null)
            {
                try
                {
                    RequireOwned(existing, normalizedExecutable);
                }
                finally
                {
                    Marshal.FinalReleaseComObject(existing);
                }
                rules.Remove(RuleName);
            }

            var rule = (INetFwRule)Activator.CreateInstance(
                Type.GetTypeFromCLSID(
                    new Guid("2C5BC43E-3369-4C33-AB0C-BE9469677AF4"),
                    throwOnError: true)!)!;
            try
            {
                rule.Name = RuleName;
                rule.Description = RuleDescription;
                rule.ApplicationName = normalizedExecutable;
                rule.Protocol = TcpProtocol;
                rule.LocalPorts = port.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
                rule.RemoteAddresses = "*";
                rule.Direction = InboundDirection;
                rule.InterfaceTypes = "All";
                rule.Enabled = true;
                rule.Grouping = RuleGrouping;
                rule.Profiles = AllProfiles;
                rule.EdgeTraversal = false;
                rule.Action = AllowAction;
                rules.Add(rule);
            }
            finally
            {
                Marshal.FinalReleaseComObject(rule);
            }

            RequireReadyCore(rules, port, normalizedExecutable);
        }
        finally
        {
            Marshal.FinalReleaseComObject(rules);
            Marshal.FinalReleaseComObject(policy);
        }
    }

    internal static void RequireReady(int port, string executablePath)
    {
        RequirePort(port);
        var normalizedExecutable = Path.GetFullPath(executablePath);
        var (policy, rules) = OpenRules();
        try
        {
            RequireReadyCore(rules, port, normalizedExecutable);
        }
        finally
        {
            Marshal.FinalReleaseComObject(rules);
            Marshal.FinalReleaseComObject(policy);
        }
    }

    internal static bool IsReady(int port, string executablePath)
    {
        try
        {
            RequireReady(port, executablePath);
            return true;
        }
        catch (Exception error) when (
            error is COMException or
                UnauthorizedAccessException or
                InvalidOperationException or
                PlatformNotSupportedException)
        {
            return false;
        }
    }

    internal static void RemoveOwned(string executablePath)
    {
        var normalizedExecutable = Path.GetFullPath(executablePath);
        var (policy, rules) = OpenRules();
        try
        {
            var existing = TryGetRule(rules);
            if (existing is null) return;
            try
            {
                RequireOwned(existing, normalizedExecutable);
            }
            finally
            {
                Marshal.FinalReleaseComObject(existing);
            }
            rules.Remove(RuleName);
            if (TryGetRule(rules) is { } retained)
            {
                Marshal.FinalReleaseComObject(retained);
                throw new InvalidOperationException(
                    "Windows retained the managed Vita stream-boundary firewall rule.");
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(rules);
            Marshal.FinalReleaseComObject(policy);
        }
    }

    internal static void RequireAbsentOrOwned(string executablePath)
    {
        var normalizedExecutable = Path.GetFullPath(executablePath);
        var (policy, rules) = OpenRules();
        try
        {
            var existing = TryGetRule(rules);
            if (existing is null) return;
            try
            {
                RequireOwned(existing, normalizedExecutable);
            }
            finally
            {
                Marshal.FinalReleaseComObject(existing);
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(rules);
            Marshal.FinalReleaseComObject(policy);
        }
    }

    private static void RequireReadyCore(
        INetFwRules rules,
        int port,
        string executablePath)
    {
        var rule = TryGetRule(rules) ?? throw new InvalidOperationException(
            "The managed Vita stream-boundary firewall rule is missing.");
        try
        {
            RequireOwned(rule, executablePath);
            var expectedPort = port.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
            if (rule.Protocol != TcpProtocol ||
                !string.Equals(rule.LocalPorts, expectedPort, StringComparison.Ordinal) ||
                !string.Equals(
                    rule.RemoteAddresses,
                    "*",
                    StringComparison.OrdinalIgnoreCase) ||
                rule.Direction != InboundDirection ||
                rule.Action != AllowAction ||
                !rule.Enabled ||
                rule.EdgeTraversal ||
                rule.Profiles != AllProfiles ||
                !string.Equals(
                    rule.InterfaceTypes,
                    "All",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The managed Vita stream-boundary firewall rule does not match its secure configuration.");
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(rule);
        }
    }

    private static void RequireOwned(INetFwRule rule, string executablePath)
    {
        if (!string.Equals(rule.Name, RuleName, StringComparison.Ordinal) ||
            !string.Equals(
                rule.Description,
                RuleDescription,
                StringComparison.Ordinal) ||
            !string.Equals(
                rule.Grouping,
                RuleGrouping,
                StringComparison.Ordinal) ||
            !string.Equals(
                Path.GetFullPath(rule.ApplicationName ?? string.Empty),
                executablePath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"A firewall rule named '{RuleName}' exists but is not owned by this installation. It was left unchanged.");
        }
    }

    private static (INetFwPolicy2 Policy, INetFwRules Rules) OpenRules()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Windows Firewall integration is available only on Windows.");
        }
        var policy = (INetFwPolicy2)Activator.CreateInstance(
            Type.GetTypeFromCLSID(
                new Guid("E2B3C97F-6AE1-41AC-817A-F6F92166D7DD"),
                throwOnError: true)!)!;
        try
        {
            return (policy, policy.Rules);
        }
        catch
        {
            Marshal.FinalReleaseComObject(policy);
            throw;
        }
    }

    private static INetFwRule? TryGetRule(INetFwRules rules)
    {
        return TryGetRule(rules, RuleName);
    }

    private static INetFwRule? TryGetRule(
        INetFwRules rules,
        string ruleName)
    {
        try
        {
            return rules.Item(ruleName);
        }
        catch (Exception error) when (IsMissingRuleError(error))
        {
            return null;
        }
    }

    // The firewall automation interface reports a missing rule with
    // HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND). Depending on the exact COM
    // dispatch path, .NET maps that HRESULT either to COMException or to
    // FileNotFoundException. Classify the HRESULT instead of the managed
    // exception type so a normal first install (where no old rule exists) is
    // idempotent on every supported Windows build.
    internal static bool IsMissingRuleError(Exception error)
    {
        for (Exception? current = error;
             current is not null;
             current = current.InnerException)
        {
            if (current.HResult == ErrorFileNotFound) return true;
        }
        return false;
    }

    /// <summary>
    /// Exercises the real Windows Firewall automation dispatch without
    /// creating or changing a rule. A unique name must be absent, and its
    /// HRESULT must pass through the same production classifier used during
    /// first-install rule creation.
    /// </summary>
    internal static void VerifyMissingRuleInteropForSelfTest()
    {
        var (policy, rules) = OpenRules();
        try
        {
            var impossibleName =
                $"{RuleName}.self-test.{Guid.NewGuid():N}";
            var unexpected = TryGetRule(rules, impossibleName);
            if (unexpected is null) return;
            Marshal.FinalReleaseComObject(unexpected);
            throw new InvalidOperationException(
                "Windows Firewall unexpectedly returned a rule for a unique self-test name.");
        }
        finally
        {
            Marshal.FinalReleaseComObject(rules);
            Marshal.FinalReleaseComObject(policy);
        }
    }

    private static void RequirePort(int port)
    {
        if (port is < 1 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }
    }

    [ComImport]
    [Guid("98325047-C671-4174-8D81-DEFCD3F03186")]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface INetFwPolicy2
    {
        [DispId(7)]
        INetFwRules Rules { get; }
    }

    [ComImport]
    [Guid("9C4C6277-5027-441E-AFAE-CA1F542DA009")]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface INetFwRules
    {
        [DispId(2)]
        void Add(INetFwRule rule);

        [DispId(3)]
        void Remove(string name);

        [DispId(4)]
        INetFwRule Item(string name);
    }

    [ComImport]
    [Guid("AF230D27-BABA-4E42-ACED-F524F22CFCE2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface INetFwRule
    {
        [DispId(1)]
        string Name { get; set; }

        [DispId(2)]
        string Description { get; set; }

        [DispId(3)]
        string ApplicationName { get; set; }

        [DispId(5)]
        int Protocol { get; set; }

        [DispId(6)]
        string LocalPorts { get; set; }

        [DispId(9)]
        string RemoteAddresses { get; set; }

        [DispId(11)]
        int Direction { get; set; }

        [DispId(13)]
        string InterfaceTypes { get; set; }

        [DispId(14)]
        bool Enabled { get; set; }

        [DispId(15)]
        string Grouping { get; set; }

        [DispId(16)]
        int Profiles { get; set; }

        [DispId(17)]
        bool EdgeTraversal { get; set; }

        [DispId(18)]
        int Action { get; set; }
    }
}
