using System.Runtime.ExceptionServices;
using System.Text.Json;
using Microsoft.Win32;
using PresencePlus;

try
{
    if (args.Length == 2 && args[0] == "--screenshots")
    {
        DocumentationScreenshots.Capture(Path.GetFullPath(args[1]));
        return;
    }

    if (args.Length >= 2 && args[0] == "--hardware")
    {
        var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PresencePlus");
        var saved = new AppSettingsStore(Path.Combine(data, "settings.json")).Load();
        using var hardware = new MatterOutput(Path.GetFullPath(args[1]), Path.Combine(data, "Matter"));
        var device = await hardware.InspectAsync(CancellationToken.None);
        if (!device.Paired) throw new InvalidOperationException("Hardware check requires an existing pairing.");
        Console.WriteLine($"Reconnected: {device.Name}, endpoint {device.Endpoint}");
        try
        {
            foreach (var status in StatusOptions.All)
            {
                var state = (saved with { Status = status.Id }).GetLedState();
                await hardware.SetAsync(state, CancellationToken.None);
                Console.WriteLine($"Matter acknowledged {status.Label}: {state.Color}");
                await Task.Delay(750);
            }
            var customColorState = (saved with { Status = "Available" }).WithStatusColor("Available", "#3366FF");
            await hardware.SetAsync(customColorState.GetLedState(), CancellationToken.None);
            Console.WriteLine("Matter acknowledged custom color #3366FF.");
            await Task.Delay(750);
        }
        finally
        {
            await hardware.SetAsync(saved.GetLedState(), CancellationToken.None);
        }
        Console.WriteLine("Saved selection restored.");
        return;
    }

    var checks = 0;
    void Equal<T>(T expected, T actual, string name)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{name}: expected {expected}, got {actual}");
        checks++;
    }

    void Throws<TException>(Action action, string name) where TException : Exception
    {
        try { action(); }
        catch (TException) { checks++; return; }
        throw new InvalidOperationException($"{name}: expected {typeof(TException).Name}");
    }

    async Task ThrowsAsync<TException>(Func<Task> action, string name) where TException : Exception
    {
        try { await action(); }
        catch (TException) { checks++; return; }
        throw new InvalidOperationException($"{name}: expected {typeof(TException).Name}");
    }

    Equal(7, StatusOptions.All.Count, "seven manual statuses");
    foreach (var (id, label, color) in new[]
    {
    ("Available", "Available", "#00FF00"), ("Busy", "Busy", "#FF8000"),
    ("DoNotDisturb", "Do not disturb", "#FF0000"), ("InMeeting", "In a meeting", "#FF0000"),
    ("Presenting", "Presenting", "#800080"), ("Away", "Away", "#FFFF00"),
    ("Offline", "Off", "#000000")
})
    {
        var status = StatusOptions.Get(id);
        Equal(label, status.Label, $"{id} label");
        Equal(new LedState(id, color), status.Led, $"{id} Matter state");
    }
    Throws<ArgumentException>(() => StatusOptions.Get("not-a-status"), "invalid status is rejected");
    Throws<ArgumentException>(() => new AppSettings { Status = "not-a-status" }.Validate(), "invalid saved status rejected");
    foreach (var value in new[] { -1, 0, 101 })
        Throws<ArgumentException>(() => new AppSettings { BrightnessPercent = value }.Validate(), "invalid brightness rejected");
    new AppSettings().Validate();
    checks++;
    var palette = new AppSettings().WithStatusColor("Available", "#123abc").WithStatusColor("DoNotDisturb", "#315FFF");
    Equal("#123ABC", palette.ColorFor("Available"), "custom colors normalized");
    Equal("#00FF00", new AppSettings().ColorFor("Available"), "editing colors does not mutate defaults");
    Equal("#FF0000", palette.ColorFor("InMeeting"), "similar default colors remain independently configurable");
    Equal(new LedState("Available", "#123ABC"), palette.GetLedState(), "custom color used in light state");
    foreach (var color in new[] { "red", "#123", "#GG0000", "123ABC", "#12345678", "#12345 " })
        Throws<ArgumentException>(() => palette.WithStatusColor("Busy", color), "malformed color rejected");
    Throws<ArgumentException>(() => palette.WithStatusColor("Unknown", "#123456"), "unknown palette status rejected");
    Throws<ArgumentException>(() => palette.WithStatusColor("Offline", "#FFFFFF"), "Off cannot become a colored on command");
    Equal("#000000", palette.ColorFor("Offline"), "Off retains actual power-off color");

    var startupKeyPath = $@"Software\PresencePlus.Checks\Startup-{Guid.NewGuid():N}";
    try
    {
        using var startupKey = Registry.CurrentUser.CreateSubKey(startupKeyPath);
        const string oldExecutable = @"C:\Old app\PresencePlus.exe";
        const string newExecutable = @"C:\New app 2\PresencePlus.exe";
        Equal(false, LoginStartup.RefreshEnabled(null, newExecutable), "missing registry key does not enable startup");
        Equal(false, LoginStartup.RefreshEnabled(startupKey, newExecutable), "first launch preserves startup opt-out");
        Equal<object?>(null, startupKey.GetValue("PresencePlus"), "first launch does not create startup entry");
        LoginStartup.SetEnabled(startupKey, true, oldExecutable);
        Equal($"\"{oldExecutable}\" --minimized", startupKey.GetValue("PresencePlus"), "startup quotes executable paths with spaces");
        Equal(true, LoginStartup.IsEnabled(startupKey, oldExecutable), "startup recognizes registered executable");
        Equal(false, LoginStartup.IsEnabled(startupKey, newExecutable), "stale path is not shown as enabled for new copy");
        Equal(true, LoginStartup.RefreshEnabled(startupKey, newExecutable), "launching moved copy preserves existing startup choice");
        Equal($"\"{newExecutable}\" --minimized", startupKey.GetValue("PresencePlus"), "launching moved copy replaces old startup path");
        Equal(true, LoginStartup.IsEnabled(startupKey, newExecutable), "startup points to moved copy");
        Equal(true, LoginStartup.RefreshEnabled(startupKey, newExecutable), "repeat launch retains startup");
        Equal(true, LoginStartup.IsEnabled(startupKey, newExecutable.ToUpperInvariant()), "startup path comparison ignores case");
        foreach (var executable in new string?[] { null, "PresencePlus.exe", @"C:\Tools\dotnet.exe" })
        {
            Throws<InvalidOperationException>(() => LoginStartup.RefreshEnabled(startupKey, executable), "invalid launch host cannot replace startup");
            Equal($"\"{newExecutable}\" --minimized", startupKey.GetValue("PresencePlus"), "invalid host preserves existing startup entry");
        }
        LoginStartup.SetEnabled(startupKey, false, newExecutable);
        Equal(false, LoginStartup.RefreshEnabled(startupKey, oldExecutable), "launching another copy respects disabled startup");
        Equal<object?>(null, startupKey.GetValue("PresencePlus"), "disabled startup stays removed");
        LoginStartup.SetEnabled(startupKey, true, newExecutable);
        Equal(true, LoginStartup.IsEnabled(startupKey, newExecutable), "explicit enable uses current executable");
        using var readOnlyStartupKey = Registry.CurrentUser.OpenSubKey(startupKeyPath) ??
            throw new InvalidOperationException("Startup test registry key is missing.");
        Equal(true, LoginStartup.RefreshEnabled(readOnlyStartupKey, newExecutable), "current registration needs no write on repeat launch");
        Throws<UnauthorizedAccessException>(() => LoginStartup.RefreshEnabled(readOnlyStartupKey, oldExecutable), "startup repair permission failure surfaces");
        Equal($"\"{newExecutable}\" --minimized", startupKey.GetValue("PresencePlus"), "failed repair preserves previous registration");
    }
    finally
    {
        Registry.CurrentUser.DeleteSubKeyTree(startupKeyPath, throwOnMissingSubKey: false);
    }

    var directory = Path.Combine(Path.GetTempPath(), $"PresencePlus-checks-{Guid.NewGuid():N}");
    var path = Path.Combine(directory, "settings.json");
    try
    {
        var store = new AppSettingsStore(path);
        Equal(new AppSettings(), store.Load(), "first launch defaults");
        var saved = new AppSettings { Status = "Presenting", BrightnessPercent = 45, StartMinimized = true };
        store.Save(saved);
        Equal(saved, store.Load(), "settings and selection survive restart");
        Equal(false, File.Exists(path + ".tmp"), "atomic save leaves no temporary file");
        var replaced = saved with { Status = "Offline" };
        store.Save(replaced);
        Equal(replaced, store.Load(), "existing settings replaced");
        Throws<ArgumentException>(() => store.Save(saved with { Status = "bad" }), "invalid save rejected");
        Equal(replaced, store.Load(), "invalid save preserves previous settings");
        store.Save(palette);
        Equal("#123ABC", store.Load().ColorFor("Available"), "custom color survives restart");
        Equal("#315FFF", store.Load().ColorFor("DoNotDisturb"), "multiple color overrides survive restart");
        Equal("#FFFF00", store.Load().ColorFor("Away"), "missing overrides retain defaults");
        File.WriteAllText(path, """{"Status":"Busy","LedUrl":"http://old-device.test/led","StartMinimized":true}""");
        Equal(new AppSettings { Status = "Busy", StartMinimized = true }, store.Load(), "old URL ignored while preserving user choices");
        File.WriteAllText(path, "{");
        Throws<JsonException>(() => store.Load(), "malformed settings surfaced");
        File.WriteAllText(path, "null");
        Throws<InvalidDataException>(() => store.Load(), "empty settings surfaced");
        File.WriteAllText(path, """{"Status":"bad"}""");
        Throws<ArgumentException>(() => store.Load(), "unsupported saved status surfaced");
        foreach (var json in new[]
        {
            """{"StatusColors":null}""",
            """{"StatusColors":{"Available":null}}""",
            """{"StatusColors":{"Available":"red"}}""",
            """{"StatusColors":{"Unknown":"#112233"}}""",
            """{"StatusColors":{"Offline":"#FFFFFF"}}"""
        })
        {
            File.WriteAllText(path, json);
            Throws<ArgumentException>(() => store.Load(), "invalid saved color map surfaced");
        }
        var privatePath = Path.Combine(directory, "private");
        MatterOutput.EnsurePrivateStorage(privatePath);
        Equal(true, new DirectoryInfo(privatePath).GetAccessControl().AreAccessRulesProtected, "fabric storage does not inherit broad permissions");
        Directory.Delete(privatePath);
    }
    finally
    {
        if (File.Exists(path)) File.Delete(path);
        if (Directory.Exists(directory)) Directory.Delete(directory);
    }

    var output = new FakeMatter();
    var clock = new ManualClock();
    var controller = new LedController(output, clock);
    await controller.SynchronizeAsync(CancellationToken.None);
    Equal(1, output.States.Count, "initial selection delivered");
    await controller.SynchronizeAsync(CancellationToken.None);
    Equal(1, output.States.Count, "unchanged state not resent on every timer tick");
    controller.Configure(new() { StartMinimized = true });
    await controller.SynchronizeAsync(CancellationToken.None);
    Equal(1, output.States.Count, "UI preference changes do not resend");
    controller.Configure(new() { Status = "Presenting" });
    await controller.SynchronizeAsync(CancellationToken.None);
    Equal("#800080", output.States[^1].Color, "manual selection changes color");
    controller.Configure(new() { Status = "Presenting", BrightnessPercent = 50 });
    await controller.SynchronizeAsync(CancellationToken.None);
    Equal(50, output.States[^1].BrightnessPercent, "brightness delivered");
    controller.Retry();
    await controller.SynchronizeAsync(CancellationToken.None);
    Equal(4, output.States.Count, "explicit resend reapplies current state");

    output.Failure = new MatterException("DeviceUnavailable", "Synthetic device offline.");
    controller.Configure(new() { Status = "Busy" });
    await ThrowsAsync<MatterException>(() => controller.SynchronizeAsync(CancellationToken.None), "device errors surface");
    output.Failure = null;
    await controller.SynchronizeAsync(CancellationToken.None);
    Equal(6, output.States.Count, "failed writes retry without changing selection");
    Equal("Busy", output.States[^1].Status, "retry sends intended selection");

    output.Failure = new OperationCanceledException("Synthetic cancellation");
    controller.Configure(new() { Status = "Away" });
    await ThrowsAsync<OperationCanceledException>(() => controller.SynchronizeAsync(CancellationToken.None), "cancellation surfaces");
    output.Failure = null;
    await controller.SynchronizeAsync(CancellationToken.None);
    Equal(8, output.States.Count, "canceled write retried");

    var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    output.Pending = pending.Task;
    controller.Configure(new() { Status = "DoNotDisturb" });
    var inFlight = controller.SynchronizeAsync(CancellationToken.None);
    controller.Configure(new() { Status = "Offline" });
    pending.SetResult();
    Equal(LightConnectionState.Updating, await inFlight, "stale write not reported as latest selection");
    output.Pending = null;
    await controller.SynchronizeAsync(CancellationToken.None);
    Equal(new LedState("Offline", "#000000"), output.States[^1], "newest choice wins after slow write");
    output.ConnectionVersion++;
    await controller.SynchronizeAsync(CancellationToken.None);
    Equal(11, output.States.Count, "helper restart forces current selection delivery");
    clock.Advance(TimeSpan.FromSeconds(31));
    await controller.SynchronizeAsync(CancellationToken.None);
    Equal(12, output.States.Count, "periodic reassertion recovers light restarts");
    using var canceled = new CancellationTokenSource();
    canceled.Cancel();
    await ThrowsAsync<OperationCanceledException>(() => controller.SynchronizeAsync(canceled.Token), "shutdown cancellation honored");
    Throws<ArgumentException>(() => controller.Configure(new() { BrightnessPercent = 0 }), "invalid controller settings rejected");
    var colorsBefore = output.States.Count;
    var custom = new AppSettings { Status = "Offline" };
    custom = custom.WithStatusColor("Available", "#123ABC");
    controller.Configure(custom);
    await controller.SynchronizeAsync(CancellationToken.None);
    Equal(colorsBefore, output.States.Count, "editing inactive status does not resend current selection");
    controller.Configure(custom with { Status = "Available" });
    Equal(LightConnectionState.Connected, await controller.SynchronizeAsync(CancellationToken.None), "acknowledged custom color is connected");
    Equal("#123ABC", output.States[^1].Color, "selecting customized status uses saved color");
    custom = (custom with { Status = "Available" }).WithStatusColor("Available", "#654321");
    controller.Configure(custom);
    await controller.SynchronizeAsync(CancellationToken.None);
    Equal(colorsBefore + 2, output.States.Count, "editing active status resends even with same status and brightness");
    Equal("#654321", output.States[^1].Color, "active color change reaches output");
    var colorPending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    output.Pending = colorPending.Task;
    controller.Configure(custom.WithStatusColor("Available", "#010203"));
    var pendingColor = controller.SynchronizeAsync(CancellationToken.None);
    controller.Configure(custom.WithStatusColor("Available", "#AABBCC"));
    colorPending.SetResult();
    Equal(LightConnectionState.Updating, await pendingColor, "in-flight color changes tracked");
    output.Pending = null;
    await controller.SynchronizeAsync(CancellationToken.None);
    Equal("#AABBCC", output.States[^1].Color, "latest custom color wins after slow write");
    controller.Configure(new());
    await controller.SynchronizeAsync(CancellationToken.None);
    Equal("#00FF00", output.States[^1].Color, "resetting palette reapplies default color");
    using (var missing = new MatterOutput(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
        Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))))
    {
        await ThrowsAsync<MatterException>(() => missing.InspectAsync(CancellationToken.None), "missing helper produces actionable error");
        Equal(false, missing.IsRunning, "missing helper never reported as running");
    }

    Exception? uiFailure = null;
    var uiThread = new Thread(() =>
    {
        var uiDirectory = Path.Combine(Path.GetTempPath(), $"PresencePlus-ui-checks-{Guid.NewGuid():N}");
        var uiPath = Path.Combine(uiDirectory, "settings.json");
        try
        {
            var store = new AppSettingsStore(uiPath);
            var matter = new FakeMatter();
            var led = new LedController(matter);
            var uiSettings = new AppSettings().WithStatusColor("Presenting", "#123ABC");
            using var form = new MainForm(store, uiSettings, led, matter, false);
            Equal(7, StatusOptions.All.SelectMany(option => form.Controls.Find($"status-{option.Id}", true)).Count(),
                "window exposes all status buttons");
            Equal(1, form.Controls.Find("pair-light", true).Length, "Matter pairing control available");
            Equal(1, form.Controls.Find("status-colors", true).Length, "color customization available");
            Equal(0, FindControls(form).OfType<TextBox>().Count(field => field.Parent is not UpDownBase), "no LED URL input remains");
            var menu = form.ContextMenuStrip ?? throw new InvalidOperationException("Status menu missing.");
            var presenting = menu.Items.OfType<ToolStripMenuItem>().Single(item => item.Name == "tray-Presenting");
            presenting.PerformClick();
            Equal("Presenting", store.Load().Status, "tray selection persisted");
            Equal(true, presenting.Checked, "tray selection checked");
            Equal("Presenting (selected)", form.Controls.Find("status-Presenting", true).Single().AccessibleName,
                "tray selection reflected in window");
            led.SynchronizeAsync(CancellationToken.None).GetAwaiter().GetResult();
            Equal("#123ABC", matter.States.Single().Color, "tray selection drives custom Matter output");
            Equal(true, FindControls(form).Any(control => control.AccessibleName == "Presenting color #123ABC"),
                "window swatch uses customized color");
            using (var icon = (form.Icon ?? throw new InvalidOperationException("Status icon missing.")).ToBitmap())
                Equal(ColorTranslator.FromHtml("#123ABC").ToArgb(), icon.GetPixel(icon.Width / 2, icon.Height / 2).ToArgb(),
                    "status icon uses customized color");
            using (var colors = new StatusColorsForm(uiSettings))
            {
                Equal(6, FindControls(colors).OfType<Button>().Count(button => button.Name.StartsWith("color-")),
                    "each lit status has a color picker");
                Equal("#123ABC", colors.Controls.Find("color-Presenting", true).Single().Text, "color picker previews saved palette");
                Equal(0, colors.Controls.Find("color-Offline", true).Length, "Off stays a power action");
                colors.Show();
                Application.DoEvents();
                foreach (var button in FindControls(colors).OfType<Button>())
                    Equal(true, colors.ClientRectangle.Contains(colors.RectangleToClient(button.RectangleToScreen(button.ClientRectangle))),
                        "palette controls fit within dialog");
                Equal(Color.Black, colors.Controls.Find("color-Available", true).Single().ForeColor,
                    "bright swatches have readable text");
                ((Button)colors.Controls.Find("reset-colors", true).Single()).PerformClick();
                Equal("#800080", colors.Settings.ColorFor("Presenting"), "reset updates draft palette");
                Equal("#123ABC", uiSettings.ColorFor("Presenting"), "draft reset does not mutate live palette");
                ((Button)colors.Controls.Find("cancel-colors", true).Single()).PerformClick();
                Equal(DialogResult.Cancel, colors.DialogResult, "cancel discards draft rather than accepting");
            }
            menu.Items.OfType<ToolStripMenuItem>().Single(item => item.Name == "tray-Offline").PerformClick();
            Equal(false, presenting.Checked, "previous tray status unchecked");
            Equal("Offline", store.Load().Status, "off available from tray");
            using (var pairing = new PairingForm(matter, CancellationToken.None))
                Equal(2, FindControls(pairing).OfType<TextBox>().Count(field => field.UseSystemPasswordChar), "both credentials masked");
            form.Show();
            Application.DoEvents();
            var connection = form.Controls.Find("connection-state", true).Single();
            var reconnect = (Button)form.Controls.Find("reconnect-light", true).Single();
            Equal("Connected", connection.Text, "compact connected indicator after acknowledged write");
            Equal("Light: Connected", connection.AccessibleName, "connection state readable without relying on color");
            foreach (var (code, expected) in new[] { ("DeviceUnavailable", "Retrying"), ("NotPaired", "Not paired"), ("NotInstalled", "Unavailable") })
            {
                matter.Failure = new MatterException(code, "Synthetic connection detail.");
                reconnect.PerformClick();
                Equal(expected, connection.Text, "compact connection error state");
                Equal(true, connection.AccessibleDescription?.Contains("Synthetic connection detail.") == true,
                    "actionable error detail remains accessible");
            }
            matter.Failure = null;
            reconnect.PerformClick();
            Equal("Connected", connection.Text, "connection indicator recovers");
            _ = form.Handle;
            form.Close();
            Equal(false, form.IsDisposed, "window close keeps tray app alive");
            menu.Items.OfType<ToolStripMenuItem>().Single(item => item.Text == "Exit").PerformClick();
            Equal(true, form.IsDisposed, "tray Exit disposes app");
        }
        catch (Exception exception) { uiFailure = exception; }
        finally
        {
            if (File.Exists(uiPath)) File.Delete(uiPath);
            if (Directory.Exists(uiDirectory)) Directory.Delete(uiDirectory);
        }
    });
    uiThread.SetApartmentState(ApartmentState.STA);
    uiThread.Start();
    uiThread.Join();
    if (uiFailure is not null) ExceptionDispatchInfo.Capture(uiFailure).Throw();

    if (args.Length == 2 && args[0] == "--node")
    {
        var fixture = Path.Combine(Path.GetTempPath(), $"PresencePlus-ipc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(fixture, "dist"));
        try
        {
            File.Copy(Path.GetFullPath(args[1]), Path.Combine(fixture, "node.exe"));
            File.Copy(Path.Combine(AppContext.BaseDirectory, "matter-fixture.js"), Path.Combine(fixture, "dist", "main.js"));
            using var matter = new MatterOutput(fixture, Path.Combine(fixture, "storage"));
            Equal(true, (await matter.InspectAsync(CancellationToken.None)).Paired, "real process IPC inspect");
            await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => matter.SetAsync(StatusOptions.Get("Available").Led, CancellationToken.None)));
            checks++;
            var version = matter.ConnectionVersion;
            await matter.PairAsync("synthetic-code", "synthetic-network", "synthetic-password", CancellationToken.None);
            Equal(true, matter.ConnectionVersion > version, "pairing restarts helper to enable Bluetooth");
            await ThrowsAsync<MatterException>(() => matter.SetAsync(new("fixture", "#010101"), CancellationToken.None), "out-of-order response rejected");
            Equal(false, matter.IsRunning, "protocol failure discards helper");
            await matter.SetAsync(StatusOptions.Get("Available").Led, CancellationToken.None);
            Equal(true, matter.IsRunning, "helper restarts after protocol failure");
            using var cancel = new CancellationTokenSource(100);
            await ThrowsAsync<OperationCanceledException>(() => matter.SetAsync(new("fixture", "#020202"), cancel.Token), "in-flight request cancellation");
            Equal(false, matter.IsRunning, "canceled reply cannot corrupt next request");
            await matter.SetAsync(StatusOptions.Get("Available").Led, CancellationToken.None);
            await ThrowsAsync<MatterException>(() => matter.SetAsync(new("fixture", "#030303"), CancellationToken.None), "unexpected helper exit surfaced");
            matter.Dispose();
            Equal(false, matter.IsRunning, "disposed helper no longer running");
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"IPC check failed: {exception}");
            throw;
        }
        finally
        {
            for (var attempt = 0; ; attempt++)
            {
                try { Directory.Delete(fixture, recursive: true); break; }
                catch (Exception exception) when (attempt < 5 && exception is IOException or UnauthorizedAccessException)
                {
                    await Task.Delay(250);
                }
            }
        }
    }

    Console.WriteLine($"All {checks} PresencePlus checks passed.");

    static IEnumerable<Control> FindControls(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var descendant in FindControls(child)) yield return descendant;
        }
    }
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    Environment.ExitCode = 1;
}

sealed class ManualClock : TimeProvider
{
    private DateTimeOffset now = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance(TimeSpan amount) => now += amount;
}

sealed class FakeMatter : IMatterOutput
{
    public List<LedState> States { get; } = [];
    public bool IsRunning { get; set; } = true;
    public long ConnectionVersion { get; set; } = 1;
    public Exception? Failure { get; set; }
    public Task? Pending { get; set; }

    public async Task SetAsync(LedState state, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        States.Add(state);
        if (Failure is not null) ExceptionDispatchInfo.Capture(Failure).Throw();
        if (Pending is not null) await Pending.WaitAsync(cancellationToken);
    }

    public Task<MatterDeviceInfo> InspectAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new MatterDeviceInfo(true, "Synthetic light", 1));
    public Task<MatterDeviceInfo> PairAsync(string pairingCode, string ssid, string password, CancellationToken cancellationToken) =>
        InspectAsync(cancellationToken);
}
