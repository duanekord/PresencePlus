using System.Runtime.InteropServices;
using System.Security;
using System.Net.NetworkInformation;

namespace PresencePlus;

public sealed class MainForm : Form
{
    private readonly AppSettingsStore store;
    private readonly LedController controller;
    private readonly IMatterOutput matter;
    private readonly NotifyIcon tray;
    private readonly ContextMenuStrip trayMenu = new();
    private readonly Dictionary<string, Button> buttons = [];
    private readonly Dictionary<string, ToolStripMenuItem> menuItems = [];
    private readonly Panel swatch = new() { Width = 32, Height = 32, BorderStyle = BorderStyle.FixedSingle };
    private readonly Label currentStatus = new() { AutoSize = true, Font = new Font("Segoe UI", 16, FontStyle.Bold) };
    private readonly Label delivery = new() { AutoSize = true, Text = "Connecting", Name = "connection-state", AccessibleName = "Light connection" };
    private readonly Panel connectionDot = new() { Width = 12, Height = 12, Margin = new Padding(3, 7, 5, 3), AccessibleName = "Connection indicator" };
    private readonly ToolTip details = new();
    private Color connectionColor = Color.DarkOrange;
    private readonly NumericUpDown brightness = new() { Minimum = 1, Maximum = 100, Value = 100, AccessibleName = "Light brightness percent" };
    private readonly CheckBox minimizeOnStart = new() { AutoSize = true, Text = "Start minimized to the tray" };
    private readonly CheckBox startAtLogin = new() { AutoSize = true, Text = "Start PresencePlus when I sign into Windows" };
    private readonly CheckBox shareTablet = new() { AutoSize = true, Text = "Share read-only tablet display", Name = "share-tablet" };
    private readonly Button tabletLink = new() { AutoSize = true, Text = "Tablet link...", Enabled = false, Name = "tablet-link" };
    private readonly Button resetTabletLink = new() { AutoSize = true, Text = "Reset viewing link...", Enabled = false, Name = "reset-tablet-link" };
    private readonly TabletDisplay tabletDisplay;
    private bool changingTabletSharing;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    private readonly CancellationTokenSource shutdown = new();
    private AppSettings settings;
    private Icon? statusIcon;
    private bool updating;
    private bool sending;
    private bool exiting;
    private bool notified;
    private bool pairing;
    private DateTimeOffset retryAfter;
    private readonly bool startMinimized;

    public MainForm(AppSettingsStore store, AppSettings settings, LedController controller, IMatterOutput matter, bool startMinimized,
        TabletDisplay? tabletDisplay = null)
    {
        this.store = store;
        this.settings = settings;
        this.controller = controller;
        this.matter = matter;
        this.tabletDisplay = tabletDisplay ?? new TabletDisplay(settings);
        this.startMinimized = startMinimized || settings.StartMinimized;
        controller.Configure(settings);
        Text = "PresencePlus";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(520, 660);
        MinimumSize = new Size(460, 680);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1, RowCount = 9,
            AutoScroll = true
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var header = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        header.Controls.Add(swatch);
        header.Controls.Add(currentStatus);
        layout.Controls.Add(header);
        var connection = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        connectionDot.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(connectionColor);
            e.Graphics.FillEllipse(brush, 1, 1, 10, 10);
        };
        connection.Controls.AddRange([connectionDot, delivery]);
        layout.Controls.Add(connection);
        SetConnection(LightConnectionState.Connecting, "Connecting to your light.");

        var choices = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        choices.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        choices.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        foreach (var option in StatusOptions.All)
        {
            var button = new Button
            {
                Text = option.Label, TextAlign = ContentAlignment.MiddleCenter,
                AutoSize = false, Height = 42, Dock = DockStyle.Fill, Margin = new Padding(4),
                AccessibleName = option.Label, Name = $"status-{option.Id}", Tag = option.Id
            };
            button.Click += (_, _) =>
            {
                if (!updating) SaveSelection(this.settings with { Status = option.Id });
            };
            buttons.Add(option.Id, button);
            choices.Controls.Add(button);
            var item = new ToolStripMenuItem(option.Label) { Name = $"tray-{option.Id}" };
            item.Click += (_, _) => SaveSelection(this.settings with { Status = option.Id });
            menuItems.Add(option.Id, item);
        }
        layout.Controls.Add(choices);
        var lightOptions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        lightOptions.Controls.Add(new Label { Text = "Brightness (%)", AutoSize = true });
        brightness.ValueChanged += (_, _) =>
        {
            if (!updating) SaveSelection(this.settings with { BrightnessPercent = (int)brightness.Value });
        };
        lightOptions.Controls.Add(brightness);
        var colors = new Button { Text = "Colors...", AutoSize = true, Name = "status-colors", AccessibleName = "Customize status colors" };
        colors.Click += (_, _) =>
        {
            using var dialog = new StatusColorsForm(this.settings);
            if (dialog.ShowDialog(this) == DialogResult.OK)
                SaveSelection(this.settings with { StatusColors = dialog.Settings.StatusColors });
        };
        lightOptions.Controls.Add(colors);
        layout.Controls.Add(lightOptions);
        var deviceActions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        var pair = new Button { Text = "Pair light...", AutoSize = true, AccessibleName = "Pair Matter light", Name = "pair-light" };
        pair.Click += async (_, _) =>
        {
            if (pairing) return;
            pairing = true;
            pair.Enabled = false;
            try
            {
                var device = await matter.InspectAsync(shutdown.Token);
                if (device.Paired)
                {
                    SetConnection(LightConnectionState.Connected, $"Paired to {device.Name}. Use Reconnect to resend your status.");
                    return;
                }
                using var dialog = new PairingForm(matter, shutdown.Token);
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    controller.Retry();
                    retryAfter = DateTimeOffset.MinValue;
                    SetConnection(LightConnectionState.Updating, "Light paired. Applying your status.");
                }
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
            catch (MatterException exception) { if (!IsDisposed) ShowError(exception.Message); }
            finally { pairing = false; pair.Enabled = true; }
            await SynchronizeAsync();
        };
        var retry = new Button { Text = "Reconnect", AutoSize = true, Name = "reconnect-light" };
        retry.Click += async (_, _) => { controller.Retry(); retryAfter = DateTimeOffset.MinValue; await SynchronizeAsync(); };
        deviceActions.Controls.AddRange([pair, retry]);
        layout.Controls.Add(deviceActions);

        var preferences = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown };
        minimizeOnStart.CheckedChanged += (_, _) =>
        {
            if (!updating) SaveSelection(this.settings with { StartMinimized = minimizeOnStart.Checked });
        };
        startAtLogin.Checked = LoginStartup.IsEnabled();
        startAtLogin.CheckedChanged += (_, _) => ChangeLoginStartup();
        preferences.Controls.AddRange([minimizeOnStart, startAtLogin]);
        layout.Controls.Add(preferences);
        var tabletOptions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        shareTablet.Checked = settings.TabletDisplayEnabled;
        resetTabletLink.Enabled = settings.TabletDisplayKey is not null;
        shareTablet.CheckedChanged += async (_, _) => await ChangeTabletSharingAsync();
        tabletLink.Click += (_, _) =>
        {
            try
            {
                using var dialog = new TabletLinkForm(this.tabletDisplay.GetLinks());
                dialog.ShowDialog(this);
            }
            catch (Exception exception) when (exception is NetworkInformationException or InvalidOperationException)
            {
                ShowError(exception.Message);
            }
        };
        resetTabletLink.Click += (_, _) =>
        {
            if (MessageBox.Show(this, "Reset the private viewing link? All existing tablet bookmarks will stop working. " +
                "Open Tablet link... afterwards and update each tablet.", "PresencePlus",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
                ResetTabletDisplayLink();
        };
        tabletOptions.Controls.AddRange([shareTablet, tabletLink, resetTabletLink]);
        layout.Controls.Add(tabletOptions);
        layout.Controls.Add(new Label
        {
            Text = "Close the window to keep running in the tray.",
            AutoSize = true, MaximumSize = new Size(480, 0)
        });
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        var hide = new Button { Text = "Minimize to tray", AutoSize = true, Name = "minimize-to-tray" };
        hide.Click += (_, _) => HideToTray();
        var exit = new Button { Text = "Exit", AutoSize = true, Name = "exit-app" };
        exit.Click += (_, _) => ExitApp();
        actions.Controls.AddRange([hide, exit]);
        layout.Controls.Add(actions);
        Controls.Add(layout);

        trayMenu.Items.Add("Open PresencePlus", null, (_, _) => RestoreWindow());
        trayMenu.Items.Add(new ToolStripSeparator());
        foreach (var item in menuItems.Values) trayMenu.Items.Add(item);
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("Exit", null, (_, _) => ExitApp());
        ContextMenuStrip = trayMenu;
        tray = new NotifyIcon { Text = "PresencePlus", ContextMenuStrip = trayMenu };
        tray.DoubleClick += (_, _) => RestoreWindow();
        RefreshSelection();
        tray.Visible = true;
        timer.Tick += async (_, _) => await SynchronizeAsync();
        Shown += async (_, _) =>
        {
            if (this.startMinimized) HideToTray();
            timer.Start();
            if (this.settings.TabletDisplayEnabled) await ChangeTabletSharingAsync();
            await SynchronizeAsync();
        };
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized) HideToTray(); };
        FormClosing += (_, e) =>
        {
            if (!exiting && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                HideToTray();
            }
        };
    }

    private void SaveSelection(AppSettings next)
    {
        try
        {
            next.Validate();
            var selectionChanged = settings.GetLedState() != next.GetLedState();
            store.Save(next);
            controller.Configure(next);
            settings = next;
            tabletDisplay.Update(settings);
            retryAfter = DateTimeOffset.MinValue;
            if (selectionChanged) SetConnection(LightConnectionState.Updating, "Applying your selection.");
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or SecurityException)
        {
            ShowError(exception.Message);
        }
        RefreshSelection();
    }

    private void RefreshSelection()
    {
        updating = true;
        try
        {
            var selected = StatusOptions.Get(settings.Status);
            foreach (var option in StatusOptions.All)
            {
                var active = option.Id == selected.Id;
                buttons[option.Id].BackColor = active ? SystemColors.Highlight : SystemColors.Control;
                buttons[option.Id].ForeColor = active ? SystemColors.HighlightText : SystemColors.ControlText;
                buttons[option.Id].AccessibleName = option.Label + (active ? " (selected)" : "");
                menuItems[option.Id].Checked = option.Id == selected.Id;
            }
            currentStatus.Text = selected.Label;
            swatch.BackColor = ColorTranslator.FromHtml(settings.ColorFor(selected.Id));
            swatch.AccessibleName = $"{selected.Label} color {settings.ColorFor(selected.Id)}";
            tray.Text = $"PresencePlus - {selected.Label}";
            var old = statusIcon;
            statusIcon = CreateStatusIcon(swatch.BackColor);
            Icon = statusIcon;
            tray.Icon = statusIcon;
            old?.Dispose();
            brightness.Value = settings.BrightnessPercent;
            minimizeOnStart.Checked = settings.StartMinimized;
        }
        finally { updating = false; }
    }

    private async Task SynchronizeAsync()
    {
        if (sending || exiting || pairing || DateTimeOffset.UtcNow < retryAfter) return;
        sending = true;
        try
        {
            var state = await controller.SynchronizeAsync(shutdown.Token);
            if (!IsDisposed) SetConnection(state, state == LightConnectionState.Connected
                ? "Your selected status is applied to the light." : "Applying your latest selection.");
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
        catch (MatterException exception)
        {
            if (!IsDisposed)
            {
                retryAfter = DateTimeOffset.UtcNow.AddSeconds(5);
                var state = exception.Code switch
                {
                    "NotPaired" => LightConnectionState.NotPaired,
                    "NotInstalled" or "StorageUnavailable" => LightConnectionState.Unavailable,
                    _ => LightConnectionState.Retrying
                };
                SetConnection(state, exception.Message + (state == LightConnectionState.Retrying ? " Retrying automatically." : ""));
            }
        }
        finally { sending = false; }
    }

    private void SetConnection(LightConnectionState state, string description)
    {
        delivery.Text = state == LightConnectionState.NotPaired ? "Not paired" : state.ToString();
        delivery.AccessibleName = $"Light: {delivery.Text}";
        connectionColor = state switch
        {
            LightConnectionState.Connected => Color.ForestGreen,
            LightConnectionState.Unavailable => Color.Firebrick,
            LightConnectionState.NotPaired => Color.Gray,
            _ => Color.DarkOrange
        };
        delivery.AccessibleDescription = description;
        details.SetToolTip(delivery, description);
        details.SetToolTip(connectionDot, description);
        connectionDot.Invalidate();
    }

    private void ChangeLoginStartup()
    {
        if (updating) return;
        try { LoginStartup.SetEnabled(startAtLogin.Checked); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or SecurityException)
        {
            updating = true;
            startAtLogin.Checked = !startAtLogin.Checked;
            updating = false;
            ShowError(exception.Message);
        }
    }

    private async Task ChangeTabletSharingAsync()
    {
        if (changingTabletSharing || exiting) return;
        changingTabletSharing = true;
        var wasRunning = tabletDisplay.IsRunning;
        var enabled = shareTablet.Checked;
        shareTablet.Enabled = false;
        tabletLink.Enabled = false;
        resetTabletLink.Enabled = false;
        try
        {
            var candidate = settings.WithTabletSharing(enabled);
            tabletDisplay.Update(candidate);
            if (enabled) await tabletDisplay.StartAsync();
            else await tabletDisplay.StopAsync();
            // Status can change while the listener starts; preserve the latest selection.
            var next = settings with { TabletDisplayEnabled = enabled, TabletDisplayKey = candidate.TabletDisplayKey };
            store.Save(next);
            settings = next;
            tabletDisplay.Update(settings);
        }
        catch (Exception exception) when (exception is IOException or System.Net.Sockets.SocketException or
            UnauthorizedAccessException or InvalidOperationException or ArgumentException or SecurityException)
        {
            var message = $"Tablet sharing could not be changed: {exception.Message}\n\n" +
                $"Check settings folder access and whether TCP port {TabletDisplay.DefaultPort} is already in use.";
            try
            {
                tabletDisplay.Update(settings);
                if (wasRunning) await tabletDisplay.StartAsync();
                else await tabletDisplay.StopAsync();
            }
            catch (Exception rollback) when (rollback is IOException or System.Net.Sockets.SocketException or
                UnauthorizedAccessException or InvalidOperationException)
            {
                message += $"\n\nThe previous listener state could not be restored: {rollback.Message}";
            }
            if (!IsDisposed && !exiting) ShowError(message);
        }
        finally
        {
            if (!IsDisposed)
            {
                shareTablet.Checked = tabletDisplay.IsRunning;
                shareTablet.Enabled = true;
                tabletLink.Enabled = tabletDisplay.IsRunning;
                resetTabletLink.Enabled = tabletDisplay.IsRunning || settings.TabletDisplayKey is not null;
            }
            changingTabletSharing = false;
        }
        if (exiting || IsDisposed) tabletDisplay.Dispose();
    }

    internal void ResetTabletDisplayLink() => SaveSelection(settings.WithNewTabletDisplayKey());

    private void ShowError(string message)
    {
        RestoreWindow();
        MessageBox.Show(this, message, "PresencePlus", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private void HideToTray()
    {
        Hide();
        if (notified) return;
        notified = true;
        tray.ShowBalloonTip(3000, "PresencePlus is still running", "Use the tray icon to change status or exit.", ToolTipIcon.Info);
    }

    private void RestoreWindow()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void ExitApp()
    {
        exiting = true;
        timer.Stop();
        shutdown.Cancel();
        tabletDisplay.Dispose();
        tray.Visible = false;
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsDisposed)
        {
            exiting = true;
            shutdown.Cancel();
            tabletDisplay.Dispose();
            timer.Dispose();
            tray.Dispose();
            trayMenu.Dispose();
            statusIcon?.Dispose();
            details.Dispose();
            shutdown.Dispose();
        }
        base.Dispose(disposing);
    }

    private static Icon CreateStatusIcon(Color color)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(color);
            graphics.FillEllipse(brush, 3, 3, 26, 26);
            graphics.DrawEllipse(Pens.Gray, 3, 3, 26, 26);
        }
        var handle = bitmap.GetHicon();
        try
        {
            using var borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally { DestroyIcon(handle); }
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
