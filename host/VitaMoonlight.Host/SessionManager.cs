namespace VitaMoonlight.Host;

internal sealed record SessionStartResult(string DisplayName, int Width, int Height, int Fps, string RecoveryFile);

internal sealed class SessionManager
{
    private readonly DisplayTopologyService displays = new();

    internal SessionStartResult Start(int width, int height, int fps)
    {
        ValidateStreamMode(width, height, fps);
        using var sessionLock = AcquireLock();
        if (File.Exists(HostStatePaths.RecoveryFile))
        {
            throw new InvalidOperationException(
                $"A pending display recovery record already exists at {HostStatePaths.RecoveryFile}. Run `session recover` first."
            );
        }

        var settings = HostSettings.Load();
        var recovery = displays.CaptureRecovery(width, height, fps);
        displays.SaveRecovery(recovery);

        try
        {
            if (!settings.HostMode.Equals("apollo", StringComparison.OrdinalIgnoreCase))
            {
                DisplayWizardAdapter.Locate(settings.DisplayWizardPath).PrepareMode(width, height, fps);
            }

            var selected = ActivateWithRetry(settings.DisplayMatch, width, height, fps);
            displays.SaveRecovery(recovery with { SelectedDisplay = selected.FriendlyName });
            return new SessionStartResult(selected.FriendlyName, width, height, fps, HostStatePaths.RecoveryFile);
        }
        catch (Exception startError)
        {
            try
            {
                displays.Restore();
                DisplayTopologyService.ClearRecovery();
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

    internal bool RestoreIfPending()
    {
        using var sessionLock = AcquireLock();
        if (!File.Exists(HostStatePaths.RecoveryFile))
        {
            return false;
        }
        displays.Restore();
        DisplayTopologyService.ClearRecovery();
        return true;
    }

    internal bool HasPendingRecovery => File.Exists(HostStatePaths.RecoveryFile);

    private DisplayDescriptor ActivateWithRetry(string? displayMatch, int width, int height, int fps)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                return displays.ActivateVirtualDisplay(displayMatch, width, height, fps);
            }
            catch (InvalidOperationException error) when (error.Message.StartsWith("No virtual display", StringComparison.Ordinal))
            {
                lastError = error;
                Thread.Sleep(500);
            }
        }
        throw new InvalidOperationException("The virtual display did not become available within 10 seconds.", lastError);
    }

    private static FileStream AcquireLock()
    {
        Directory.CreateDirectory(HostStatePaths.Root);
        try
        {
            return new FileStream(HostStatePaths.LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException error)
        {
            throw new InvalidOperationException("Another Vita Moonlight session operation is already running.", error);
        }
    }

    private static void ValidateStreamMode(int width, int height, int fps)
    {
        if (width < 64 || width > 7680 || height < 64 || height > 4320 || fps < 24 || fps > 240)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "The requested stream mode is outside supported bounds.");
        }
    }
}
