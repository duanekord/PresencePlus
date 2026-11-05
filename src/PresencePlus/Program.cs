using System.Security;
using System.Security.Principal;
using System.Text.Json;

namespace PresencePlus;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args is ["--check-package"])
        {
            using var form = new StatusColorsForm(new AppSettings());
            _ = form.Handle;
            Console.WriteLine("PresencePlus Windows runtime loaded.");
            return;
        }
        using var identity = WindowsIdentity.GetCurrent();
        using var instance = new Mutex(true, $@"Local\PresencePlus-{identity.User?.Value}", out var first);
        if (!first)
        {
            MessageBox.Show("PresencePlus is already running. Open it from the notification area (including hidden tray icons).",
                "PresencePlus", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            LoginStartup.RefreshEnabled();
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PresencePlus", "settings.json");
            var store = new AppSettingsStore(path);
            var settings = store.Load();
            using var matter = new MatterOutput(Path.Combine(AppContext.BaseDirectory, "Matter"),
                Path.Combine(Path.GetDirectoryName(path)!, "Matter"));
            Application.Run(new MainForm(store, settings, new LedController(matter),
                matter, args.Contains("--minimized", StringComparer.OrdinalIgnoreCase)));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            JsonException or ArgumentException or InvalidOperationException or SecurityException)
        {
            MessageBox.Show($"PresencePlus could not start:\n\n{exception.Message}\n\nCheck %LOCALAPPDATA%\\PresencePlus\\settings.json, folder access, and Windows startup permissions.",
                "PresencePlus", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
