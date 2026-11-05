using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Win32;

namespace PresencePlus;

public sealed record AppSettings
{
    public string Status { get; init; } = "Available";
    public int BrightnessPercent { get; init; } = 100;
    public bool StartMinimized { get; init; }
    public ImmutableDictionary<string, string> StatusColors { get; init; } = ImmutableDictionary<string, string>.Empty;

    public string ColorFor(string status)
    {
        var option = StatusOptions.Get(status);
        return status == "Offline" ? option.Color : StatusColors.GetValueOrDefault(status, option.Color);
    }

    public LedState GetLedState() => new(Status, ColorFor(Status), BrightnessPercent);

    public AppSettings WithStatusColor(string status, string color)
    {
        Validate();
        ArgumentNullException.ThrowIfNull(color);
        StatusOptions.Get(status);
        if (status == "Offline") throw new ArgumentException("Off always turns the light off.");
        var next = this with { StatusColors = StatusColors.SetItem(status, color.ToUpperInvariant()) };
        next.Validate();
        return next;
    }

    public void Validate()
    {
        StatusOptions.Get(Status);
        if (BrightnessPercent is < 1 or > 100)
            throw new ArgumentException("Brightness must be between 1 and 100 percent.");
        if (StatusColors is null) throw new ArgumentException("Status colors must be a color map.");
        foreach (var (status, color) in StatusColors)
        {
            StatusOptions.Get(status);
            if (status == "Offline") throw new ArgumentException("Off always turns the light off.");
            if (color is null || color.Length != 7 || color[0] != '#' ||
                color.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") >= 0)
                throw new ArgumentException("Status colors must use #RRGGBB format.");
        }
    }
}

public sealed class AppSettingsStore(string path)
{
    public AppSettings Load()
    {
        if (!File.Exists(path)) return new();
        var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ??
            throw new InvalidDataException("PresencePlus settings cannot be empty.");
        settings.Validate();
        return settings;
    }

    public void Save(AppSettings settings)
    {
        settings.Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}

public static class LoginStartup
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "PresencePlus";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(Key);
        return key?.GetValue(Name) is string;
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Key);
        if (!enabled)
        {
            key.DeleteValue(Name, false);
            return;
        }
        var executable = Environment.ProcessPath;
        if (executable is null || !Path.GetFileName(executable).Equals("PresencePlus.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Publish PresencePlus to a permanent folder and run PresencePlus.exe before enabling Windows startup.");
        key.SetValue(Name, $"\"{executable}\" --minimized", RegistryValueKind.String);
    }
}
