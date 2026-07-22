using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace VitaMoonlight.Host;

internal sealed class HostControlPanel : Form
{
    private static readonly Color Accent = Color.FromArgb(48, 118, 255);
    private static readonly Color Surface = Color.FromArgb(247, 249, 252);
    private static readonly Color Ink = Color.FromArgb(28, 34, 46);
    private readonly RichTextBox output = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        BackColor = Color.FromArgb(22, 26, 34),
        ForeColor = Color.FromArgb(226, 232, 240),
        Font = new Font(FontFamily.GenericMonospace, 9.25f),
        BorderStyle = BorderStyle.None,
    };
    private readonly ToolStripStatusLabel status = new("Ready");
    private readonly List<Control> actionControls = new();
    private readonly HashSet<Control> administratorControls = new();
    private readonly ComboBox hostMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    private readonly CheckBox integrateAllApps = new()
    {
        AutoSize = true,
        Text = "Automatically switch to the Vita display for every Sunshine application",
    };
    private readonly CheckBox forceSdr = new()
    {
        AutoSize = true,
        Text = "Force SDR for Vita virtual-display sessions",
    };
    private readonly TextBox displayMatch = new() { Width = 320 };
    private readonly bool isAdministrator;

    private HostControlPanel()
    {
        isAdministrator = IsAdministrator();
        Text = "Vita Moonlight Host";
        MinimumSize = new Size(900, 700);
        Size = new Size(1060, 790);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5f);
        BackColor = Surface;
        AutoScaleMode = AutoScaleMode.Dpi;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(0),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 220));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        root.Controls.Add(CreateHeader(), 0, 0);
        root.Controls.Add(CreateAdministratorBanner(), 0, 1);
        root.Controls.Add(CreateTabs(), 0, 2);
        root.Controls.Add(CreateActivityPanel(), 0, 3);

        var statusStrip = new StatusStrip { SizingGrip = false };
        statusStrip.Items.Add(status);
        root.Controls.Add(statusStrip, 0, 4);
        Controls.Add(root);

        LoadSettings();
        Shown += async (_, _) => await RunCommandAsync(new[] { "doctor" }, allowNonZeroExit: true);
    }

    internal static void Run()
    {
        HideOwnedConsoleWindow();
        ApplicationConfiguration.Initialize();
        Application.Run(new HostControlPanel());
    }

    private static Control CreateHeader()
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(25, 35, 58), Padding = new Padding(24, 15, 24, 12) };
        var title = new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 20f),
            ForeColor = Color.White,
            Text = "Vita Moonlight Host",
            Location = new Point(22, 12),
        };
        var subtitle = new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = Color.FromArgb(190, 204, 228),
            Text = "Configure Sunshine, the Vita-native virtual display, SDR color, controllers, and recovery.",
            Location = new Point(25, 53),
        };
        panel.Controls.Add(title);
        panel.Controls.Add(subtitle);
        return panel;
    }

    private Control CreateAdministratorBanner()
    {
        var elevated = isAdministrator;
        var panel = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(20, 9, 20, 8),
            BackColor = elevated ? Color.FromArgb(231, 248, 239) : Color.FromArgb(255, 246, 224),
        };
        panel.Controls.Add(new Label
        {
            AutoSize = true,
            Padding = new Padding(0, 7, 10, 0),
            ForeColor = elevated ? Color.FromArgb(22, 101, 52) : Color.FromArgb(145, 91, 0),
            Text = elevated
                ? "Administrator mode is enabled. Setup and display actions are available."
                : "Administrator mode is required for setup and display changes.",
        });
        if (!elevated)
        {
            var elevate = CreateButton("Restart as Administrator", ButtonKind.Primary, 190);
            elevate.Click += (_, _) => RelaunchElevated();
            panel.Controls.Add(elevate);
            actionControls.Add(elevate);
        }
        return panel;
    }

    private Control CreateTabs()
    {
        var tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Padding = new Point(18, 6),
            Margin = new Padding(18, 12, 18, 8),
        };
        tabs.TabPages.Add(CreateOverviewPage());
        tabs.TabPages.Add(CreateStreamingPage());
        tabs.TabPages.Add(CreateDisplaysPage());
        tabs.TabPages.Add(CreateSupportPage());
        return tabs;
    }

    private TabPage CreateOverviewPage()
    {
        var page = CreatePage("Overview");
        AddHeading(page, "Ready the PC for Vita streaming", "The recommended setup is automatic and safe to repeat after an update.");

        var actions = CreateActionRow();
        AddCommandButton(actions, "Apply recommended setup", ButtonKind.Primary,
            async () => await ApplyConfigurationAsync(restartSunshine: true), 220);
        AddCommandButton(actions, "Run health check", ButtonKind.Secondary,
            async () => { await RunCommandAsync(new[] { "doctor" }, allowNonZeroExit: true); }, 180,
            requiresAdministrator: false);
        AddCommandButton(actions, "Restart Sunshine", ButtonKind.Secondary,
            async () => { await RunCommandAsync(new[] { "host", "restart", "--host", "sunshine" }); }, 180,
            "Restart Sunshine now? Any active stream will disconnect.");
        AddPageControl(page, actions);

        AddPageControl(page, CreateInfoCard(
            "Recommended Vita profile",
            "960 x 544  •  60 FPS  •  8 Mbps  •  H.264 SDR\n" +
            "Choose any Sunshine application—including Steam Big Picture. The host switches to the virtual display before capture and restores your desktop afterward."));
        AddPageControl(page, CreateInfoCard(
            "In-stream controls",
            "Open the Vita overlay with START + L + R. It can force-close the foreground Windows game, end the Sunshine app, or recover a failed display/host in addition to the normal stream controls. Double-press PS remains the forced escape to Vita LiveArea."));
        return page;
    }

    private TabPage CreateStreamingPage()
    {
        var page = CreatePage("Streaming");
        AddHeading(page, "Streaming behavior", "These settings configure Sunshine's global display lifecycle and compatibility launcher.");

        var form = new TableLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            ColumnCount = 2,
            Padding = new Padding(4, 6, 4, 8),
        };
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        hostMode.Items.AddRange(new object[] { "Sunshine", "Apollo" });
        AddField(form, "Streaming host", hostMode);
        AddField(form, "Virtual display match", displayMatch);
        var optionsRow = form.RowCount++;
        form.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        form.Controls.Add(new Label { AutoSize = true, Text = "Options", ForeColor = Ink, Padding = new Padding(0, 7, 10, 0) }, 0, optionsRow);
        var options = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Dock = DockStyle.Fill };
        options.Controls.Add(integrateAllApps);
        options.Controls.Add(forceSdr);
        form.Controls.Add(options, 1, optionsRow);
        AddPageControl(page, form);

        var actions = CreateActionRow();
        AddCommandButton(actions, "Save and apply", ButtonKind.Primary,
            async () => await ApplyConfigurationAsync(restartSunshine: true), 180);
        AddCommandButton(actions, "Save without restart", ButtonKind.Secondary,
            async () => await ApplyConfigurationAsync(restartSunshine: false), 170);
        AddPageControl(page, actions);
        AddPageControl(page, CreateInfoCard(
            "Why “every application” is recommended",
            "Sunshine's native display manager covers Desktop, Steam Big Picture, and custom games, then restores the physical desktop when every client disconnects—even if Steam remains open for resume."));
        return page;
    }

    private TabPage CreateDisplaysPage()
    {
        var page = CreatePage("Displays");
        AddHeading(page, "Virtual display and recovery", "Preview the Vita display safely, inspect Windows targets, or restore the physical desktop.");
        var actions = CreateActionRow();
        AddCommandButton(actions, "Preview 960x544 for 15 seconds", ButtonKind.Primary,
            async () => { await RunCommandAsync(new[] { "session", "test", "--width", "960", "--height", "544", "--fps", "60", "--seconds", "15" }); }, 250,
            "The physical monitor may go blank for 15 seconds. The original layout will then be restored automatically.");
        AddCommandButton(actions, "List displays", ButtonKind.Secondary,
            async () => { await RunCommandAsync(new[] { "display", "list" }); }, 150,
            requiresAdministrator: false);
        AddCommandButton(actions, "Disable idle virtual display", ButtonKind.Secondary,
            async () => { await RunCommandAsync(new[] { "display", "disable-virtual" }); }, 220,
            "Disable only the idle Vita virtual monitor and keep the physical monitor active?");
        AddCommandButton(actions, "Emergency display reset", ButtonKind.Warning,
            async () => { await RunCommandAsync(new[] { "emergency", "recover-display" }); }, 200,
            "Disconnect active streams, activate the physical monitor, reload the virtual display driver, and restart Sunshine?");
        AddPageControl(page, actions);

        var maintenance = CreateActionRow();
        AddCommandButton(maintenance, "Install/update display driver", ButtonKind.Secondary,
            async () => { await RunCommandAsync(new[] { "driver", "install" }); }, 220,
            "Install or update the signed virtual display driver? Windows may briefly refresh connected displays.");
        AddCommandButton(maintenance, "Reload display driver", ButtonKind.Secondary,
            async () => { await RunCommandAsync(new[] { "driver", "reload" }); }, 180,
            "Reload the virtual display driver now? Connected displays may briefly flicker.");
        AddCommandButton(maintenance, "Session status", ButtonKind.Secondary,
            async () => { await RunCommandAsync(new[] { "session", "status" }); }, 150,
            requiresAdministrator: false);
        AddPageControl(page, maintenance);
        return page;
    }

    private TabPage CreateSupportPage()
    {
        var page = CreatePage("Help & recovery");
        AddHeading(page, "Help and recovery", "Everything required for normal use is available here; a terminal is optional.");
        var actions = CreateActionRow();
        AddCommandButton(actions, "Run diagnostics", ButtonKind.Primary,
            async () => { await RunCommandAsync(new[] { "doctor" }, allowNonZeroExit: true); }, 170,
            requiresAdministrator: false);
        AddCommandButton(actions, "Install recovery safeguard", ButtonKind.Secondary,
            async () => { await RunCommandAsync(new[] { "recovery", "install" }); }, 210,
            "Install or repair the logon recovery task for interrupted display sessions?");
        AddCommandButton(actions, "Install stream rescue agent", ButtonKind.Secondary,
            async () => { await RunCommandAsync(new[] { "agent", "install" }); }, 210,
            "Install or repair the background hotkey agent used by the Vita overlay for game and display recovery?");
        AddCommandButton(actions, "Rescue agent status", ButtonKind.Secondary,
            async () => { await RunCommandAsync(new[] { "agent", "status" }, allowNonZeroExit: true); }, 180,
            requiresAdministrator: false);
        AddDocumentButton(actions, "Open setup guide", "README.md");
        AddDocumentButton(actions, "Open acceptance test", "END_TO_END_TEST.md");
        AddDocumentButton(actions, "Open release checklist", "FINAL_RELEASE_CHECKLIST.md");
        AddDocumentButton(actions, "Compatibility", "COMPATIBILITY.md");
        AddDocumentButton(actions, "Vita settings guide", "VITA_SETTINGS_GUIDE.md");
        AddPageControl(page, actions);
        AddPageControl(page, CreateInfoCard(
            "Black-screen recovery",
            "From the Vita overlay, first choose Close Windows game. If video does not recover, choose Recover display + Sunshine; the stream will disconnect while Windows activates the physical monitor, reloads VDD, and restarts Sunshine. Sign out and back in only if the rescue agent cannot run."));
        return page;
    }

    private Control CreateActivityPanel()
    {
        var group = new GroupBox
        {
            Text = "Activity and recommendations",
            Dock = DockStyle.Fill,
            Padding = new Padding(10),
            Margin = new Padding(18, 2, 18, 8),
        };
        group.Controls.Add(output);
        return group;
    }

    private static TabPage CreatePage(string title)
    {
        var page = new TabPage(title) { BackColor = Color.White, Padding = new Padding(16) };
        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            ColumnCount = 1,
            RowCount = 0,
            Padding = new Padding(4),
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        page.Controls.Add(body);
        page.Tag = body;
        return page;
    }

    private static void AddPageControl(TabPage page, Control control)
    {
        var body = (TableLayoutPanel)(page.Tag ?? throw new InvalidOperationException("Page body is missing."));
        var row = body.RowCount++;
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        control.Dock = DockStyle.Top;
        control.Margin = new Padding(0, 0, 0, 8);
        body.Controls.Add(control, 0, row);
    }

    private static void AddHeading(TabPage page, string title, string description)
    {
        var descriptionLabel = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(850, 0),
            ForeColor = Color.FromArgb(85, 96, 115),
            Text = description,
            Padding = new Padding(0, 2, 0, 10),
        };
        var titleLabel = new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 15f),
            ForeColor = Ink,
            Text = title,
            Padding = new Padding(0, 0, 0, 2),
        };
        var heading = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0, 0, 0, 8),
        };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        heading.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        heading.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        heading.Controls.Add(titleLabel, 0, 0);
        heading.Controls.Add(descriptionLabel, 0, 1);
        AddPageControl(page, heading);
    }

    private static FlowLayoutPanel CreateActionRow() => new()
    {
        AutoSize = true,
        Dock = DockStyle.Top,
        WrapContents = true,
        Padding = new Padding(0, 5, 0, 10),
        Margin = new Padding(0),
    };

    private static Control CreateInfoCard(string title, string text)
    {
        var card = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.FromArgb(241, 246, 255),
            Padding = new Padding(14, 12, 14, 12),
            Margin = new Padding(0, 8, 0, 0),
        };
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var body = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(850, 0),
            ForeColor = Color.FromArgb(64, 76, 98),
            Text = text,
            Padding = new Padding(0, 4, 0, 0),
        };
        var heading = new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 10.5f),
            ForeColor = Ink,
            Text = title,
        };
        card.Controls.Add(heading, 0, 0);
        card.Controls.Add(body, 0, 1);
        return card;
    }

    private static void AddField(TableLayoutPanel form, string label, Control control)
    {
        var row = form.RowCount++;
        form.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        form.Controls.Add(new Label
        {
            AutoSize = true,
            Text = label,
            ForeColor = Ink,
            Padding = new Padding(0, 7, 10, 0),
        }, 0, row);
        control.Margin = new Padding(3, 4, 3, 7);
        form.Controls.Add(control, 1, row);
    }

    private void LoadSettings()
    {
        var settings = HostSettings.Load();
        hostMode.SelectedItem = settings.HostMode.Equals("apollo", StringComparison.OrdinalIgnoreCase) ? "Apollo" : "Sunshine";
        integrateAllApps.Checked = settings.IntegrateAllSunshineApps;
        forceSdr.Checked = settings.ForceSdr;
        displayMatch.Text = settings.DisplayMatch ?? string.Empty;
    }

    private async Task ApplyConfigurationAsync(bool restartSunshine)
    {
        var selectedHost = hostMode.SelectedItem?.ToString()?.ToLowerInvariant() ?? "sunshine";
        var arguments = new List<string>
        {
            "configure", "--host", selectedHost,
            "--all-apps", integrateAllApps.Checked.ToString().ToLowerInvariant(),
            "--force-sdr", forceSdr.Checked.ToString().ToLowerInvariant(),
        };
        if (!string.IsNullOrWhiteSpace(displayMatch.Text))
        {
            arguments.Add("--display-match");
            arguments.Add(displayMatch.Text.Trim());
        }
        if (restartSunshine && selectedHost == "sunshine" &&
            !await RunCommandAsync(new[] { "host", "restart", "--host", "sunshine" }))
        {
            return;
        }
        if (!await RunCommandAsync(arguments.ToArray())) return;
        if (!await RunCommandAsync(new[] { "recovery", "install" })) return;
        if (!await RunCommandAsync(new[] { "agent", "install" })) return;
        if (restartSunshine && selectedHost == "sunshine")
        {
            await RunCommandAsync(new[] { "host", "restart", "--host", "sunshine" });
        }
    }

    private void AddCommandButton(
        Control parent,
        string text,
        ButtonKind kind,
        Func<Task> action,
        int width = 180,
        string? confirmation = null,
        bool requiresAdministrator = true)
    {
        var button = CreateButton(text, kind, width);
        button.Click += async (_, _) =>
        {
            if (confirmation is not null &&
                MessageBox.Show(this, confirmation, text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                return;
            }
            await action();
        };
        actionControls.Add(button);
        if (requiresAdministrator) administratorControls.Add(button);
        button.Enabled = !requiresAdministrator || isAdministrator;
        parent.Controls.Add(button);
    }

    private void AddDocumentButton(Control parent, string text, string fileName)
    {
        var button = CreateButton(text, ButtonKind.Secondary, 180);
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
        actionControls.Add(button);
        parent.Controls.Add(button);
    }

    private static Button CreateButton(string text, ButtonKind kind, int width)
    {
        var button = new Button
        {
            Text = text,
            Size = new Size(width, 40),
            Margin = new Padding(4),
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
        };
        button.FlatAppearance.BorderSize = kind == ButtonKind.Secondary ? 1 : 0;
        button.BackColor = kind switch
        {
            ButtonKind.Primary => Accent,
            ButtonKind.Warning => Color.FromArgb(190, 83, 52),
            _ => Color.White,
        };
        button.ForeColor = kind == ButtonKind.Secondary ? Ink : Color.White;
        button.FlatAppearance.BorderColor = Color.FromArgb(190, 198, 212);
        return button;
    }

    private async Task<bool> RunCommandAsync(string[] arguments, bool allowNonZeroExit = false)
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
            var accepted = process.ExitCode == 0 || allowNonZeroExit;
            status.Text = process.ExitCode == 0 ? "Completed successfully" : "Check completed — review the recommendation";
            if (!accepted)
            {
                status.Text = $"Action failed with exit code {process.ExitCode}";
                MessageBox.Show(this, combined, "Vita Moonlight action failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            return process.ExitCode == 0;
        }
        catch (Exception error)
        {
            output.Text = error.ToString();
            status.Text = "Action failed";
            MessageBox.Show(this, error.Message, "Vita Moonlight action failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        finally
        {
            SetBusy(false, status.Text ?? "Ready");
        }
    }

    private void SetBusy(bool busy, string message)
    {
        foreach (var control in actionControls)
        {
            control.Enabled = !busy && (isAdministrator || !administratorControls.Contains(control));
        }
        hostMode.Enabled = !busy;
        integrateAllApps.Enabled = !busy;
        forceSdr.Enabled = !busy;
        displayMatch.Enabled = !busy;
        UseWaitCursor = busy;
        status.Text = message;
    }

    private void RelaunchElevated()
    {
        try
        {
            var executable = Environment.ProcessPath
                ?? throw new InvalidOperationException("The control panel executable path is unavailable.");
            var startInfo = new ProcessStartInfo(executable) { UseShellExecute = true, Verb = "runas" };
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

    private enum ButtonKind
    {
        Primary,
        Secondary,
        Warning,
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetConsoleProcessList([Out] uint[] processList, uint processCount);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);
}
