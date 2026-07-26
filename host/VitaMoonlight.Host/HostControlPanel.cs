using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace VitaMoonlight.Host;

internal sealed class HostControlPanel : Form
{
    private const int RestartRequiredExitCode = 4;
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
        Text = "Run a health check or support action to see technical details.",
    };
    private readonly Label readinessSummary = new()
    {
        AutoSize = true,
        MaximumSize = new Size(850, 0),
        ForeColor = Color.FromArgb(64, 76, 98),
        UseMnemonic = false,
        Text = "Checking this PC…",
        Padding = new Padding(0, 4, 0, 0),
    };
    private readonly ToolStripStatusLabel status = new("Ready");
    private readonly List<Control> actionControls = new();
    private readonly HashSet<Control> administratorControls = new();
    private readonly ComboBox hostMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    private readonly CheckBox integrateAllApps = new()
    {
        AutoSize = true,
        Text = "Use the Vita display with every streamed application (recommended)",
    };
    private readonly CheckBox forceSdr = new()
    {
        AutoSize = true,
        Text = "Force SDR for Vita virtual-display sessions",
    };
    private readonly TextBox displayMatch = new()
    {
        Width = 320,
        PlaceholderText = "Leave blank for automatic selection",
    };
    private readonly bool isAdministrator;
    private readonly bool isInstalledPayload;
    private string lastTechnicalOutput = string.Empty;

    private HostControlPanel()
    {
        isAdministrator = IsAdministrator();
        isInstalledPayload =
            InstallationTrust.IsInstalledPayload(out _);
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
            RowCount = 4,
            Padding = new Padding(0),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        root.Controls.Add(CreateHeader(), 0, 0);
        root.Controls.Add(CreateAdministratorBanner(), 0, 1);
        root.Controls.Add(CreateTabs(), 0, 2);

        var statusStrip = new StatusStrip { SizingGrip = false };
        statusStrip.Items.Add(status);
        root.Controls.Add(statusStrip, 0, 3);
        Controls.Add(root);

        LoadSettings();
        Shown += async (_, _) => await RunHealthCheckAsync();
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
            UseMnemonic = false,
            Text = "Vita Moonlight Host",
            Location = new Point(22, 12),
        };
        var subtitle = new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = Color.FromArgb(190, 204, 228),
            UseMnemonic = false,
            Text = "Set up, stream, and recover your Vita connection without using a terminal.",
            Location = new Point(25, 53),
        };
        panel.Controls.Add(title);
        panel.Controls.Add(subtitle);
        return panel;
    }

    private Control CreateAdministratorBanner()
    {
        var elevated = isAdministrator;
        var setupAvailable = elevated && isInstalledPayload;
        var panel = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(20, 9, 20, 8),
            BackColor = setupAvailable
                ? Color.FromArgb(231, 248, 239)
                : Color.FromArgb(255, 246, 224),
        };
        panel.Controls.Add(new Label
        {
            AutoSize = true,
            Padding = new Padding(0, 7, 10, 0),
            ForeColor = setupAvailable
                ? Color.FromArgb(22, 101, 52)
                : Color.FromArgb(145, 91, 0),
            UseMnemonic = false,
            Text = !isInstalledPayload
                ? "Portable mode is diagnostics-only. Use the installer for setup and recovery safeguards."
                : elevated
                ? "Administrator mode is enabled. Setup and display actions are available."
                : "Administrator mode is required for setup and display changes.",
        });
        if (!elevated && isInstalledPayload)
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
        var page = CreatePage("Get started");
        AddHeading(
            page,
            "Set up this PC for Vita streaming",
            "One guided repair handles first-time setup, upgrades, and most connection problems. It is safe to run again.");

        var actions = CreateActionRow();
        AddCommandButton(actions, "Set up or repair this PC", ButtonKind.Primary,
            async () => await ApplyConfigurationAsync(
                restartSunshine: true,
                repairPrerequisites: true), 230);
        AddCommandButton(actions, "Check readiness", ButtonKind.Secondary,
            RunHealthCheckAsync, 170,
            requiresAdministrator: false);
        AddPageControl(page, actions);

        AddPageControl(page, CreateReadinessCard());
        AddPageControl(page, CreateInfoCard(
            "What happens next",
            "1. Install and open the Vita VPK.\n" +
            "2. Add this PC in Moonlight and approve the PIN in Sunshine.\n" +
            "3. Launch Steam Big Picture, Desktop, or a game. The host switches to the Vita display for the stream and restores your physical display when the session ends."));
        AddPageControl(page, CreateInfoCard(
            "Recommended starting profile",
            "960 × 544  •  60 FPS  •  8 Mbps  •  H.264  •  SDR\n" +
            "This profile matches the Vita screen and is selected for dependable Wi-Fi performance. Tune quality later from the Vita settings menu."));
        AddPageControl(page, CreateInfoCard(
            "If a game or display gets stuck",
            "On the Vita, hold START and then press L + R within one second to open the stream menu without sending the shortcut to the PC. Close the Windows game first; if video does not recover, choose Recover display + Sunshine. Double-press PS remains the forced return to Vita LiveArea."));
        return page;
    }

    private TabPage CreateStreamingPage()
    {
        var page = CreatePage("Streaming");
        AddHeading(
            page,
            "Streaming preferences",
            "The recommended defaults work for most PCs. Change these only when you use Apollo or have more than one virtual display.");

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
        AddField(form, "Streaming service", hostMode);
        AddField(form, "Preferred virtual display", displayMatch);
        var optionsRow = form.RowCount++;
        form.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        form.Controls.Add(new Label { AutoSize = true, Text = "Options", ForeColor = Ink, Padding = new Padding(0, 7, 10, 0) }, 0, optionsRow);
        var options = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Dock = DockStyle.Fill };
        options.Controls.Add(integrateAllApps);
        options.Controls.Add(forceSdr);
        form.Controls.Add(options, 1, optionsRow);
        AddPageControl(page, form);

        var actions = CreateActionRow();
        AddCommandButton(actions, "Save and restart streaming", ButtonKind.Primary,
            async () => await ApplyConfigurationAsync(restartSunshine: true), 220);
        AddCommandButton(actions, "Save for next session", ButtonKind.Secondary,
            async () => await ApplyConfigurationAsync(restartSunshine: false), 190);
        AddCommandButton(actions, "Restart Sunshine now", ButtonKind.Secondary,
            async () => { await RunCommandAsync(new[] { "host", "restart", "--host", "sunshine" }); }, 190,
            "Restart Sunshine now? Any active stream will disconnect.");
        AddPageControl(page, actions);
        AddPageControl(page, CreateInfoCard(
            "Recommended choices",
            "Leave Preferred virtual display blank unless the health check finds more than one virtual monitor. Keep every-application switching and SDR enabled: they cover Desktop, Steam, and custom games, avoid washed-out HDR color, and restore the physical desktop after the last client disconnects."));
        return page;
    }

    private TabPage CreateDisplaysPage()
    {
        var page = CreatePage("Display & recovery");
        AddHeading(
            page,
            "Restore or test the Vita display",
            "Recovery always prioritizes a working physical monitor. Testing is optional and restores the original layout automatically.");
        var actions = CreateActionRow();
        AddCommandButton(actions, "Restore physical display now", ButtonKind.Warning,
            async () => { await RunCommandAsync(new[] { "emergency", "recover-display" }); }, 200,
            "End the current stream, restore the physical monitor, reload the Vita display driver, and restart Sunshine?");
        AddCommandButton(actions, "Turn off idle Vita display", ButtonKind.Secondary,
            async () => { await RunCommandAsync(new[] { "display", "disable-virtual" }); }, 210,
            "Turn off only the idle Vita virtual monitor while keeping a physical monitor active?");
        AddPageControl(page, actions);
        AddPageControl(page, CreateInfoCard(
            "Recovery without this window",
            "Press Ctrl + Alt + Shift + F11 on the PC keyboard. The background rescue agent performs the same physical-display and driver recovery even when this control panel is closed."));

        var testing = CreateActionRow();
        AddCommandButton(testing, "Test Vita display for 15 seconds", ButtonKind.Primary,
            async () => { await RunCommandAsync(new[] { "session", "test", "--width", "960", "--height", "544", "--fps", "60", "--seconds", "15" }); }, 240,
            "Windows will test the Vita-sized display for 15 seconds, then restore the exact current physical layout.");
        AddCommandButton(testing, "Show detected displays", ButtonKind.Secondary,
            async () => { await RunCommandAsync(new[] { "display", "list" }); }, 190,
            requiresAdministrator: false);
        AddPageControl(page, testing);

        var maintenance = CreateActionRow();
        AddCommandButton(maintenance, "Repair Vita display driver", ButtonKind.Secondary,
            async () => { await RepairDisplayDriverAsync(); }, 210,
            "Repair the signed Vita virtual-display driver? Windows may briefly refresh connected displays.");
        AddCommandButton(maintenance, "Restart Vita display driver", ButtonKind.Secondary,
            async () => { await RunCommandAsync(new[] { "driver", "reload" }); }, 210,
            "Restart the Vita virtual-display driver now? Connected displays may briefly flicker.");
        AddCommandButton(maintenance, "Show current session state", ButtonKind.Secondary,
            async () => { await RunCommandAsync(new[] { "session", "status" }); }, 210,
            requiresAdministrator: false);
        AddPageControl(page, CreateSection(
            "Advanced display maintenance",
            "Use these only when readiness reports a driver or session problem.",
            maintenance));
        return page;
    }

    private TabPage CreateSupportPage()
    {
        var page = CreatePage("Diagnostics & support");
        AddHeading(
            page,
            "Diagnostics and support",
            "Start with a readiness check. Technical output and component-specific repairs are kept here so normal setup stays simple.");
        var actions = CreateActionRow();
        AddCommandButton(actions, "Run full health check", ButtonKind.Primary,
            RunHealthCheckAsync, 190,
            requiresAdministrator: false);
        AddCommandButton(actions, "Save support report…", ButtonKind.Secondary,
            SaveSupportReportAsync, 190,
            requiresAdministrator: false);
        AddCommandButton(actions, "Copy technical details", ButtonKind.Secondary,
            CopyTechnicalDetailsAsync, 190,
            requiresAdministrator: false);
        AddCommandButton(actions, "Open diagnostics folder", ButtonKind.Secondary,
            OpenDiagnosticsFolderAsync, 190,
            requiresAdministrator: false);
        AddPageControl(page, actions);
        AddPageControl(page, CreateInfoCard(
            "Safe to share?",
            "The JSON report is created only when you ask. It contains Windows and component versions, readiness results, and sanitized display names. It omits usernames, file paths, network and MAC addresses, credentials, and continuous logs. Review it before sharing."));

        var repairActions = CreateActionRow();
        AddCommandButton(repairActions, "Repair sign-in display recovery", ButtonKind.Secondary,
            async () => { await RunCommandAsync(new[] { "recovery", "install" }); }, 210,
            "Install or repair the logon recovery task for interrupted display sessions?");
        AddCommandButton(repairActions, "Repair stream rescue shortcuts", ButtonKind.Secondary,
            async () => { await RunCommandAsync(new[] { "agent", "install" }); }, 210,
            "Install or repair the background hotkey agent used by the Vita overlay for game and display recovery?");
        AddCommandButton(repairActions, "Repair controller support", ButtonKind.Secondary,
            async () => { await RepairGamepadAsync(); }, 210,
            "Install or repair ViGEmBus, then verify its service is running?");
        AddCommandButton(repairActions, "Repair Sunshine", ButtonKind.Secondary,
            async () => { await RepairSunshineAsync(); }, 210,
            "Install or repair the packaged compatible Sunshine build? Active streams will end.");
        AddCommandButton(repairActions, "Check rescue shortcuts", ButtonKind.Secondary,
            async () => { await RunCommandAsync(new[] { "agent", "status" }, allowNonZeroExit: true); }, 180,
            requiresAdministrator: false);
        AddPageControl(page, CreateSection(
            "Repair one component",
            "The guided setup on Get started already runs these repairs in the correct order. Use an individual repair only when the health check names that component.",
            repairActions));

        var guides = CreateActionRow();
        AddDocumentButton(guides, "Setup and usage guide", "README.md");
        AddDocumentButton(guides, "Community beta test", Path.Combine("host", "BETA_SMOKE_TEST.md"));
        AddDocumentButton(guides, "Logging and support guide", Path.Combine("docs", "LOGGING_AND_SUPPORT.md"));
        AddDocumentButton(guides, "Compatibility and limits", Path.Combine("host", "COMPATIBILITY.md"));
        AddDocumentButton(guides, "Vita settings guide", Path.Combine("docs", "VITA_SETTINGS_GUIDE.md"));
        AddPageControl(page, guides);
        AddPageControl(page, CreateActivityPanel());
        AddPageControl(page, CreateInfoCard(
            "Black-screen recovery",
            "From the Vita overlay, first choose Close Windows game. If video does not recover, choose Recover display + Sunshine; the stream will disconnect while Windows activates the physical monitor, reloads VDD, and restarts Sunshine. Sign out and back in only if the rescue agent cannot run."));
        return page;
    }

    private Control CreateActivityPanel()
    {
        var toggle = CreateButton(
            "Show technical details",
            ButtonKind.Secondary,
            190);
        toggle.Dock = DockStyle.Top;
        toggle.Margin = new Padding(0, 0, 0, 6);
        output.Visible = false;
        var group = new GroupBox
        {
            Text = "Technical details (advanced)",
            Height = 70,
            Padding = new Padding(10),
            Margin = new Padding(0, 4, 0, 8),
        };
        toggle.Click += (_, _) =>
        {
            output.Visible = !output.Visible;
            group.Height = output.Visible ? 280 : 70;
            toggle.Text = output.Visible
                ? "Hide technical details"
                : "Show technical details";
        };
        group.Controls.Add(output);
        group.Controls.Add(toggle);
        actionControls.Add(toggle);
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
            UseMnemonic = false,
            Text = description,
            Padding = new Padding(0, 2, 0, 10),
        };
        var titleLabel = new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 15f),
            ForeColor = Ink,
            UseMnemonic = false,
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
            UseMnemonic = false,
            Text = text,
            Padding = new Padding(0, 4, 0, 0),
        };
        var heading = new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 10.5f),
            ForeColor = Ink,
            UseMnemonic = false,
            Text = title,
        };
        card.Controls.Add(heading, 0, 0);
        card.Controls.Add(body, 0, 1);
        return card;
    }

    private Control CreateReadinessCard()
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
        card.Controls.Add(new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 10.5f),
            ForeColor = Ink,
            UseMnemonic = false,
            Text = "PC readiness",
        }, 0, 0);
        card.Controls.Add(readinessSummary, 0, 1);
        return card;
    }

    private static Control CreateSection(
        string title,
        string description,
        Control content)
    {
        var section = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Color.FromArgb(249, 250, 252),
            Padding = new Padding(12, 10, 12, 8),
            Margin = new Padding(0, 4, 0, 8),
        };
        section.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        section.Controls.Add(new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 11f),
            ForeColor = Ink,
            UseMnemonic = false,
            Text = title,
        }, 0, 0);
        section.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(850, 0),
            ForeColor = Color.FromArgb(85, 96, 115),
            UseMnemonic = false,
            Text = description,
            Padding = new Padding(0, 3, 0, 4),
        }, 0, 1);
        content.Margin = new Padding(0);
        section.Controls.Add(content, 0, 2);
        return section;
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
            UseMnemonic = false,
            Padding = new Padding(0, 7, 10, 0),
        }, 0, row);
        control.Margin = new Padding(3, 4, 3, 7);
        form.Controls.Add(control, 1, row);
    }

    private void LoadSettings()
    {
        HostSettings settings;
        try
        {
            settings = HostSettings.Load();
        }
        catch (Exception error) when (
            error is IOException or
                UnauthorizedAccessException or
                InvalidDataException or
                System.ComponentModel.Win32Exception or
                System.Text.Json.JsonException)
        {
            settings = HostSettings.Default;
            readinessSummary.Text =
                "Saved settings could not be read. Safe recommended values are shown; run Set up or repair this PC.";
            readinessSummary.ForeColor = Color.FromArgb(145, 91, 0);
            lastTechnicalOutput =
                $"Host settings could not be read:{Environment.NewLine}{error}";
            output.Text = lastTechnicalOutput;
        }
        hostMode.SelectedItem = settings.HostMode.Equals("apollo", StringComparison.OrdinalIgnoreCase) ? "Apollo" : "Sunshine";
        integrateAllApps.Checked = settings.IntegrateAllSunshineApps;
        forceSdr.Checked = settings.ForceSdr;
        displayMatch.Text = settings.DisplayMatch ?? string.Empty;
    }

    private async Task RunHealthCheckAsync()
    {
        readinessSummary.Text = "Checking Windows, streaming, display, controller, and recovery components…";
        readinessSummary.ForeColor = Color.FromArgb(64, 76, 98);
        var ready = await RunCommandAsync(
            new[] { "doctor" },
            allowNonZeroExit: true);
        readinessSummary.Text = ready
            ? "Ready to stream. Required host, display, controller, and recovery checks passed."
            : "This PC needs attention. Open Diagnostics & support for the full recommendations, then use Set up or repair this PC.";
        readinessSummary.ForeColor = ready
            ? Color.FromArgb(22, 101, 52)
            : Color.FromArgb(145, 91, 0);
    }

    private async Task SaveSupportReportAsync()
    {
        using var dialog = new SaveFileDialog
        {
            AddExtension = true,
            DefaultExt = "json",
            Filter = "JSON support report (*.json)|*.json",
            FileName =
                $"Vita-Moonlight-Support-" +
                $"{DateTime.Now:yyyyMMdd-HHmmss}.json",
            InitialDirectory = Environment.GetFolderPath(
                Environment.SpecialFolder.DesktopDirectory),
            OverwritePrompt = true,
            RestoreDirectory = true,
            Title = "Save Vita Moonlight support report",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        if (await RunCommandAsync(new[]
            {
                "support",
                "export",
                "--output",
                dialog.FileName,
            }))
        {
            // The generic command runner displays its full argument list,
            // including the user's chosen destination. Replace that output
            // with the privacy-safe report before it can be copied as
            // technical details.
            try
            {
                lastTechnicalOutput = await File.ReadAllTextAsync(dialog.FileName);
                output.Text = lastTechnicalOutput;
            }
            catch (Exception error) when (
                error is IOException or UnauthorizedAccessException)
            {
                lastTechnicalOutput =
                    "The support report was created, but the control panel could not reopen it for copying.";
                output.Text = lastTechnicalOutput;
            }
            MessageBox.Show(
                this,
                "The support report was saved. Review the JSON file before sharing it.",
                "Support report saved",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
    }

    private async Task CopyTechnicalDetailsAsync()
    {
        if (string.IsNullOrWhiteSpace(lastTechnicalOutput))
        {
            await RunHealthCheckAsync();
        }
        if (string.IsNullOrWhiteSpace(lastTechnicalOutput)) return;

        try
        {
            Clipboard.SetText(lastTechnicalOutput);
            status.Text = "Technical details copied";
        }
        catch (Exception error) when (
            error is ExternalException or
                ThreadStateException)
        {
            MessageBox.Show(
                this,
                $"Windows could not copy the details: {error.Message}",
                "Copy technical details",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private Task OpenDiagnosticsFolderAsync()
    {
        var directory = HostStatePaths.DiagnosticsDirectory;
        if (!Directory.Exists(directory))
        {
            MessageBox.Show(
                this,
                "No host rescue or recovery diagnostic files have been created. This is normal when no host-side recovery action has needed a log.",
                "Diagnostics folder",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return Task.CompletedTask;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true,
            });
        }
        catch (System.ComponentModel.Win32Exception error)
        {
            MessageBox.Show(
                this,
                $"Windows could not open the diagnostics folder: {error.Message}",
                "Diagnostics folder",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        return Task.CompletedTask;
    }

    private async Task ApplyConfigurationAsync(
        bool restartSunshine,
        bool repairPrerequisites = false)
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
        if (repairPrerequisites)
        {
            if (!await RepairGamepadAsync()) return;
            if (selectedHost == "sunshine")
            {
                if (!await RepairSunshineAsync()) return;
                if (!await RepairDisplayDriverAsync()) return;
            }
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
            if (!await RunCommandAsync(new[] { "host", "restart", "--host", "sunshine" })) return;
        }
        await RunHealthCheckAsync();
    }

    private Task<bool> RepairGamepadAsync() =>
        RunCommandAsync(new[]
        {
            "gamepad",
            "ensure-compatible",
            "--installer",
            Path.Combine(
                AppContext.BaseDirectory,
                "tools",
                "ViGEmBus",
                "ViGEmBus_1.22.0_x64_x86_arm64.exe"),
        });

    private Task<bool> RepairSunshineAsync() =>
        RunCommandAsync(new[]
        {
            "host",
            "ensure-compatible",
            "--installer",
            Path.Combine(
                AppContext.BaseDirectory,
                "tools",
                "Sunshine",
                "Sunshine-Windows-AMD64-installer.msi"),
        });

    private async Task<bool> RepairDisplayDriverAsync()
    {
        if (!await RepairVisualCppRuntimeAsync()) return false;
        return await RunCommandAsync(new[] { "driver", "install" });
    }

    private Task<bool> RepairVisualCppRuntimeAsync() =>
        RunCommandAsync(new[]
        {
            "runtime",
            "ensure-compatible",
            "--installer",
            Path.Combine(
                AppContext.BaseDirectory,
                "tools",
                "DisplayWizard",
                "VC_redist.x64.exe"),
        });

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
        button.Enabled =
            !requiresAdministrator ||
            (isAdministrator && isInstalledPayload);
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
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.System),
                        "notepad.exe"),
                    UseShellExecute = false,
                };
                startInfo.ArgumentList.Add(path);
                Process.Start(startInfo);
            }
            catch (System.ComponentModel.Win32Exception error)
            {
                MessageBox.Show(
                    this,
                    $"Windows could not open this guide: {error.Message}",
                    text,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
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
        SetBusy(true, DescribeCommand(arguments));
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
            lastTechnicalOutput = output.Text;
            if (process.ExitCode == RestartRequiredExitCode)
            {
                status.Text = "Windows restart required";
                MessageBox.Show(
                    this,
                    string.IsNullOrWhiteSpace(combined)
                        ? "Restart Windows, then open Get started and choose Set up or repair this PC again."
                        : combined,
                    "Restart required",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return false;
            }
            var accepted = process.ExitCode == 0 || allowNonZeroExit;
            status.Text = process.ExitCode == 0
                ? "Completed successfully"
                : "Check completed — review the support page";
            if (!accepted)
            {
                status.Text = "Action needs attention";
                MessageBox.Show(
                    this,
                    string.IsNullOrWhiteSpace(combined)
                        ? "Windows could not complete this action. Open Diagnostics & support for technical details."
                        : combined,
                    "Vita Moonlight needs attention",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            return process.ExitCode == 0;
        }
        catch (Exception error)
        {
            output.Text = error.ToString();
            lastTechnicalOutput = output.Text;
            status.Text = "Action failed";
            MessageBox.Show(this, error.Message, "Vita Moonlight needs attention", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        finally
        {
            SetBusy(false, status.Text ?? "Ready");
        }
    }

    private static string DescribeCommand(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0) return "Working…";
        var command = string.Join(
            " ",
            arguments.Take(Math.Min(arguments.Count, 2)))
            .ToLowerInvariant();
        return command switch
        {
            "doctor" => "Checking PC readiness…",
            "configure --host" => "Saving streaming preferences…",
            "host restart" => "Restarting Sunshine…",
            "host ensure-compatible" => "Repairing Sunshine…",
            "gamepad ensure-compatible" => "Repairing controller support…",
            "runtime ensure-compatible" => "Checking the display runtime…",
            "driver install" => "Repairing the Vita display driver…",
            "driver reload" => "Restarting the Vita display driver…",
            "display list" => "Detecting Windows displays…",
            "display disable-virtual" => "Turning off the idle Vita display…",
            "session test" => "Testing the Vita display safely…",
            "session status" => "Checking the current display session…",
            "recovery install" => "Repairing sign-in display recovery…",
            "agent install" => "Repairing rescue shortcuts…",
            "agent status" => "Checking rescue shortcuts…",
            "emergency recover-display" => "Restoring the physical display…",
            "support export" => "Creating the support report…",
            _ => "Working…",
        };
    }

    private void SetBusy(bool busy, string message)
    {
        foreach (var control in actionControls)
        {
            control.Enabled = !busy && (isAdministrator || !administratorControls.Contains(control));
            if (administratorControls.Contains(control) &&
                !isInstalledPayload)
            {
                control.Enabled = false;
            }
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
            InstallationTrust.RequireInstalledPayload(
                "Restarting the host as Administrator");
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
