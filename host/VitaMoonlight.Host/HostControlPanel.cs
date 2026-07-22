using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace VitaMoonlight.Host;

internal sealed class HostControlPanel : Form
{
    private readonly RichTextBox output = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        BackColor = Color.FromArgb(24, 26, 31),
        ForeColor = Color.Gainsboro,
        Font = new Font(FontFamily.GenericMonospace, 9.5f),
        BorderStyle = BorderStyle.FixedSingle,
    };
    private readonly ToolStripStatusLabel status = new("Ready");
    private readonly List<Button> actionButtons = new();

    private HostControlPanel()
    {
        Text = "Vita Moonlight Host Control Panel";
        MinimumSize = new Size(820, 640);
        Size = new Size(980, 720);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9f);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
            Padding = new Padding(14),
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var title = new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 18f),
            Text = "Vita Moonlight Host",
            Margin = new Padding(0, 0, 0, 3),
        };
        var introduction = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(900, 0),
            Text = "Use this panel for setup, checks, and display recovery. The 800x600 screen is the idle virtual display; disabling it here does not uninstall the driver.",
            Margin = new Padding(0, 0, 0, 10),
        };

        var administratorPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0, 0, 0, 8),
        };
        var administratorLabel = new Label
        {
            AutoSize = true,
            Padding = new Padding(0, 8, 8, 0),
            Text = IsAdministrator()
                ? "Administrator mode: enabled"
                : "Administrator mode: not enabled — setup and recovery actions may fail.",
            ForeColor = IsAdministrator() ? Color.DarkGreen : Color.DarkOrange,
        };
        administratorPanel.Controls.Add(administratorLabel);
        if (!IsAdministrator())
        {
            var elevate = new Button { AutoSize = true, Text = "Restart as Administrator" };
            elevate.Click += (_, _) => RelaunchElevated();
            administratorPanel.Controls.Add(elevate);
        }

        var actions = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            WrapContents = true,
            Margin = new Padding(0, 0, 0, 10),
        };
        AddCommandButton(actions, "Run diagnostics", new[] { "doctor" }, allowNonZeroExit: true);
        AddCommandButton(actions, "List displays", new[] { "display", "list" });
        AddCommandButton(actions, "Session status", new[] { "session", "status" });
        AddCommandButton(actions, "Run 15-second 960x544 test",
            new[] { "session", "test", "--width", "960", "--height", "544", "--fps", "60", "--seconds", "15" },
            "Run a 15-second 960x544 display test? The physical screen may go blank briefly, then its original layout will be restored automatically.");
        AddCommandButton(actions, "Stop test / restore displays", new[] { "session", "stop" },
            "Stop the test or streaming session and restore the display layout saved before it started?");
        AddCommandButton(actions, "Configure Sunshine", new[] { "configure", "--host", "sunshine" },
            "This updates Sunshine's Vita Moonlight application. Click Restart Sunshine afterward.");
        AddCommandButton(actions, "Restart Sunshine", new[] { "host", "restart", "--host", "sunshine" },
            "Restart Sunshine now? Any active Sunshine stream will disconnect.");
        AddCommandButton(actions, "Configure Apollo", new[] { "configure", "--host", "apollo" },
            "Use this only if Apollo is already installed. Restart Apollo afterward.");
        AddCommandButton(actions, "Install/update display driver", new[] { "driver", "install" },
            "Install or update the signed virtual display driver? Windows may request a reboot.");
        AddCommandButton(actions, "Reload display driver", new[] { "driver", "reload" },
            "Reload the virtual display driver now? Connected displays may briefly flicker.");
        AddCommandButton(actions, "Disable idle virtual display", new[] { "display", "disable-virtual" },
            "Disable the idle Vita virtual monitor and keep the physical display active?");
        AddCommandButton(actions, "Recover previous display layout", new[] { "session", "recover" },
            "Restore the display layout saved before the last streaming session?");
        AddCommandButton(actions, "Install recovery safeguard", new[] { "recovery", "install" },
            "Install or repair the automatic logon task that restores an interrupted display session?");
        AddDocumentButton(actions, "Open setup guide", "README.md");
        AddDocumentButton(actions, "Open acceptance guide", "END_TO_END_TEST.md");

        var statusStrip = new StatusStrip();
        statusStrip.Items.Add(status);

        root.Controls.Add(title, 0, 0);
        root.Controls.Add(introduction, 0, 1);
        root.Controls.Add(administratorPanel, 0, 2);
        root.Controls.Add(actions, 0, 3);
        root.Controls.Add(output, 0, 4);
        root.Controls.Add(statusStrip, 0, 5);
        Controls.Add(root);

        Shown += async (_, _) => await RunCommandAsync(new[] { "doctor" }, allowNonZeroExit: true);
    }

    internal static void Run()
    {
        HideOwnedConsoleWindow();
        ApplicationConfiguration.Initialize();
        Application.Run(new HostControlPanel());
    }

    private void AddCommandButton(
        Control parent,
        string text,
        string[] arguments,
        string? confirmation = null,
        bool allowNonZeroExit = false)
    {
        var button = new Button
        {
            Text = text,
            Size = new Size(210, 44),
            Margin = new Padding(4),
        };
        button.Click += async (_, _) =>
        {
            if (confirmation is not null &&
                MessageBox.Show(this, confirmation, text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                return;
            }
            await RunCommandAsync(arguments, allowNonZeroExit);
        };
        actionButtons.Add(button);
        parent.Controls.Add(button);
    }

    private void AddDocumentButton(Control parent, string text, string fileName)
    {
        var button = new Button
        {
            Text = text,
            Size = new Size(210, 44),
            Margin = new Padding(4),
        };
        button.Click += (_, _) =>
        {
            var path = Path.Combine(AppContext.BaseDirectory, fileName);
            if (!File.Exists(path))
            {
                MessageBox.Show(this, $"The installed document was not found:\n{path}", text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        };
        actionButtons.Add(button);
        parent.Controls.Add(button);
    }

    private async Task RunCommandAsync(string[] arguments, bool allowNonZeroExit = false)
    {
        SetBusy(true, $"Running: {string.Join(' ', arguments)}");
        try
        {
            var executable = Environment.ProcessPath
                ?? throw new InvalidOperationException("The control panel executable path is unavailable.");
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
            };
            foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            process.Start();
            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var combined = string.Join(
                Environment.NewLine,
                new[] { await standardOutput, await standardError }.Where(value => !string.IsNullOrWhiteSpace(value)));
            output.Text = $"> VitaMoonlight.Host.exe {string.Join(' ', arguments)}{Environment.NewLine}{Environment.NewLine}{combined}";
            if (process.ExitCode == 0 || allowNonZeroExit)
            {
                status.Text = process.ExitCode == 0 ? "Completed successfully" : "Check completed — see recommendations below";
            }
            else
            {
                status.Text = $"Command failed with exit code {process.ExitCode}";
                MessageBox.Show(this, combined, "Vita Moonlight action failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        catch (Exception error)
        {
            output.Text = error.ToString();
            status.Text = "Action failed";
            MessageBox.Show(this, error.Message, "Vita Moonlight action failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false, status.Text ?? "Ready");
        }
    }

    private void SetBusy(bool busy, string message)
    {
        foreach (var button in actionButtons) button.Enabled = !busy;
        UseWaitCursor = busy;
        status.Text = message;
    }

    private void RelaunchElevated()
    {
        try
        {
            var executable = Environment.ProcessPath
                ?? throw new InvalidOperationException("The control panel executable path is unavailable.");
            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                Verb = "runas",
            };
            startInfo.ArgumentList.Add("gui");
            Process.Start(startInfo);
            Close();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(this, "Administrator approval was cancelled.", "Vita Moonlight Host", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void HideOwnedConsoleWindow()
    {
        var processIds = new uint[4];
        if (GetConsoleProcessList(processIds, (uint)processIds.Length) == 1)
        {
            var window = GetConsoleWindow();
            if (window != IntPtr.Zero) ShowWindow(window, 0);
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetConsoleProcessList([Out] uint[] processList, uint processCount);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);
}
