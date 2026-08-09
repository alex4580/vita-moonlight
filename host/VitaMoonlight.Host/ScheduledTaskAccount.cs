using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace VitaMoonlight.Host;

internal sealed record ScheduledTaskAccountStatus(
    bool IsSameInteractiveAccount,
    string ProcessAccount,
    string? InteractiveAccount,
    string Message);

/// <summary>
/// Vita recovery tasks must run elevated in the same interactive Windows
/// account that owns the streaming desktop. An over-the-shoulder UAC prompt
/// changes the process identity without changing that desktop; accepting it
/// would register ONLOGON tasks for the administrator instead of the user.
/// </summary>
internal static class ScheduledTaskAccount
{
    private static readonly IntPtr CurrentServer = IntPtr.Zero;

    internal static ScheduledTaskAccountStatus Inspect()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new ScheduledTaskAccountStatus(
                false,
                "unavailable",
                null,
                "Interactive scheduled-task identity is available only on Windows.");
        }

        using var processIdentity = WindowsIdentity.GetCurrent();
        using var currentProcess = Process.GetCurrentProcess();
        var processAccount = processIdentity.Name;
        var interactiveAccount = ReadInteractiveAccount(
            currentProcess.SessionId);
        if (string.IsNullOrWhiteSpace(interactiveAccount))
        {
            return new ScheduledTaskAccountStatus(
                false,
                processAccount,
                null,
                "Windows did not identify an interactive user for this setup session. " +
                "Do not install from SYSTEM, a background deployment, or a disconnected session.");
        }

        var sameAccount = AccountsMatch(
            processIdentity.User,
            processAccount,
            interactiveAccount);
        return new ScheduledTaskAccountStatus(
            sameAccount,
            processAccount,
            interactiveAccount,
            sameAccount
                ? $"Recovery tasks will run in the current interactive account ({interactiveAccount})."
                : $"Setup is running as {processAccount}, but the interactive streaming desktop belongs to {interactiveAccount}. " +
                  "Windows would register Vita recovery tasks in the wrong account.");
    }

    internal static void RequireCurrentInteractiveUser(string operation)
    {
        var status = Inspect();
        if (status.IsSameInteractiveAccount) return;
        throw new InvalidOperationException(
            BuildUnsupportedAccountMessage(operation, status));
    }

    internal static string BuildUnsupportedAccountMessageForTest(
        string operation,
        string processAccount,
        string? interactiveAccount) =>
        BuildUnsupportedAccountMessage(
            operation,
            new ScheduledTaskAccountStatus(
                false,
                processAccount,
                interactiveAccount,
                interactiveAccount is null
                    ? "Windows did not identify an interactive user for this setup session."
                    : $"Setup is running as {processAccount}, but the interactive streaming desktop belongs to {interactiveAccount}."));

    internal static bool AccountsMatchForTest(
        string processAccount,
        string interactiveAccount) =>
        string.Equals(
            processAccount.Trim(),
            interactiveAccount.Trim(),
            StringComparison.OrdinalIgnoreCase);

    private static string BuildUnsupportedAccountMessage(
        string operation,
        ScheduledTaskAccountStatus status) =>
        $"{operation} cannot continue. {status.Message} " +
        "Sign in to the administrator account that will be used for Vita streaming and run setup from that account. " +
        "Elevating a standard user's setup with a different administrator password is intentionally rejected.";

    private static bool AccountsMatch(
        SecurityIdentifier? processSid,
        string processAccount,
        string interactiveAccount)
    {
        try
        {
            var interactiveSid = (SecurityIdentifier)new NTAccount(
                    interactiveAccount)
                .Translate(typeof(SecurityIdentifier));
            if (processSid is not null)
            {
                return processSid.Equals(interactiveSid);
            }
        }
        catch (IdentityNotMappedException)
        {
            // Local/cloud account providers are not all resolvable through
            // NTAccount. The stable domain-qualified name remains a safe
            // fallback for rejecting a clearly different elevation identity.
        }
        return AccountsMatchForTest(processAccount, interactiveAccount);
    }

    private static string? ReadInteractiveAccount(int sessionId)
    {
        var user = QuerySessionString(sessionId, WtsInfoClass.UserName);
        if (string.IsNullOrWhiteSpace(user)) return null;
        var domain = QuerySessionString(sessionId, WtsInfoClass.DomainName);
        return string.IsNullOrWhiteSpace(domain)
            ? user
            : $"{domain}\\{user}";
    }

    private static string? QuerySessionString(
        int sessionId,
        WtsInfoClass informationClass)
    {
        if (!WTSQuerySessionInformation(
                CurrentServer,
                sessionId,
                informationClass,
                out var buffer,
                out var bytes) ||
            buffer == IntPtr.Zero)
        {
            return null;
        }
        try
        {
            return bytes <= 2
                ? null
                : Marshal.PtrToStringUni(buffer)?.Trim();
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    private enum WtsInfoClass
    {
        UserName = 5,
        DomainName = 7,
    }

    [DllImport("Wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformation(
        IntPtr serverHandle,
        int sessionId,
        WtsInfoClass informationClass,
        out IntPtr buffer,
        out int bytesReturned);

    [DllImport("Wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);
}
