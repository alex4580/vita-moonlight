using System.ComponentModel;

namespace VitaMoonlight.Host;

internal sealed record SessionStartResult(string DisplayName, int Width, int Height, int Fps, string RecoveryFile);
internal sealed record SessionModeResult(string DisplayName, VitaDisplayMode Mode);

internal sealed class SessionManager
{
    private readonly DisplayTopologyService displays = new();

    internal SessionStartResult Start(int width, int height, int fps)
    {
        return StartCore(
            width,
            height,
            fps,
            HostSettings.Load(),
            prepareDriverMode: true,
            persistMode: false,
            activationAttempts: 20);
    }

    internal SessionStartResult PrimeNativeMode()
    {
        DriverNativeModeVerification.Invalidate();
        var mode = VitaDisplayModes.Native;
        var settings = HostSettings.Load() with
        {
            HostMode = "sunshine",
            DisplayMatch = "MTT1337",
            ForceSdr = true,
        };

        ValidateStreamMode(mode.Width, mode.Height, mode.Fps);
        using var sessionLock = AcquireLock();
        if (File.Exists(HostStatePaths.RecoveryFile))
        {
            throw new InvalidOperationException(
                $"A pending display recovery record already exists at {HostStatePaths.RecoveryFile}. Run `session recover` first."
            );
        }

        var recovery = displays.CaptureRecovery(mode.Width, mode.Height, mode.Fps);
        displays.SaveRecovery(recovery);
        DisplayDescriptor selected;
        try
        {
            selected = displays.VerifyVirtualDisplayModeSafely(
                settings.DisplayMatch,
                mode.Width,
                mode.Height,
                mode.Fps,
                settings.ForceSdr,
                persistMode: true,
                modeAttempts: 40);
            displays.SaveRecovery(recovery with { SelectedDisplay = selected.FriendlyName });
        }
        catch (Exception verificationError)
        {
            try
            {
                displays.Restore();
                DisplayTopologyService.ClearRecovery();
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
            displays.Restore();
            DisplayTopologyService.ClearRecovery();
        }
        catch (Exception restoreError)
        {
            throw new InvalidOperationException(
                "Native mode was verified, but the original physical display layout could not be restored. " +
                "The recovery record has been retained.",
                restoreError);
        }

        DriverNativeModeVerification.RecordCurrent();
        return new SessionStartResult(
            selected.FriendlyName,
            mode.Width,
            mode.Height,
            mode.Fps,
            HostStatePaths.RecoveryFile);
    }

    private SessionStartResult StartCore(
        int width,
        int height,
        int fps,
        HostSettings settings,
        bool prepareDriverMode,
        bool persistMode,
        int activationAttempts)
    {
        ValidateStreamMode(width, height, fps);
        using var sessionLock = AcquireLock();
        if (File.Exists(HostStatePaths.RecoveryFile))
        {
            throw new InvalidOperationException(
                $"A pending display recovery record already exists at {HostStatePaths.RecoveryFile}. Run `session recover` first."
            );
        }

        var recovery = displays.CaptureRecovery(width, height, fps);
        displays.SaveRecovery(recovery);

        try
        {
            if (prepareDriverMode && !settings.HostMode.Equals("apollo", StringComparison.OrdinalIgnoreCase))
            {
                DisplayWizardAdapter.Locate(settings.DisplayWizardPath).PrepareMode(width, height, fps);
            }

            var selected = ActivateWithRetry(
                settings.DisplayMatch,
                width,
                height,
                fps,
                settings.ForceSdr,
                persistMode,
                activationAttempts);
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

    internal SessionModeResult ChangeMode(int width, int height, int fps)
    {
        var mode = VitaDisplayModes.RequireSupported(width, height, fps);
        using var sessionLock = AcquireLock();
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
            settings.DisplayMatch,
            mode.Width,
            mode.Height,
            mode.Fps,
            settings.ForceSdr);
        return new SessionModeResult(selected.FriendlyName, mode);
    }

    private DisplayDescriptor ActivateWithRetry(
        string? displayMatch,
        int width,
        int height,
        int fps,
        bool forceSdr,
        bool persistMode,
        int activationAttempts)
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
                    persistMode);
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
