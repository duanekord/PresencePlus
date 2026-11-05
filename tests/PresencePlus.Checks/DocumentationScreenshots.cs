using System.Runtime.ExceptionServices;
using PresencePlus;

internal static class DocumentationScreenshots
{
    public static void Capture(string directory)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Application.SetHighDpiMode(HighDpiMode.DpiUnaware);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetDefaultFont(new Font("Segoe UI", 9));
                Directory.CreateDirectory(directory);
                var output = new FakeMatter();
                var settings = new AppSettings();
                // Render the actual forms against a fake output, never a user's light or credentials.
                using var main = new MainForm(new AppSettingsStore(Path.Combine(directory, "unused-settings.json")),
                    settings, new LedController(output), output, false);
                Save(main, Path.Combine(directory, "main.png"));
                using var pairing = new PairingForm(output, CancellationToken.None);
                Save(pairing, Path.Combine(directory, "pairing.png"));
                using var colors = new StatusColorsForm(settings);
                Save(colors, Path.Combine(directory, "colors.png"));
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        Console.WriteLine($"Saved credential-free documentation screenshots: {directory}");
    }

    private static void Save(Form form, string path)
    {
        form.Show();
        Application.DoEvents();
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        form.Hide();
    }
}
