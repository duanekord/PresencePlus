namespace PresencePlus;

public sealed record LedState(string Status, string Color, int BrightnessPercent = 100);
public sealed record StatusOption(string Id, string Label, string Color)
{
    public LedState Led => new(Id, Color);
}

public static class StatusOptions
{
    public static IReadOnlyList<StatusOption> All { get; } = Array.AsReadOnly<StatusOption>(
    [
        new("Available", "Available", "#00FF00"),
        new("Busy", "Busy", "#FF8000"),
        new("DoNotDisturb", "Do not disturb", "#FF0000"),
        new("InMeeting", "In a meeting", "#FF0000"),
        new("Presenting", "Presenting", "#800080"),
        new("Away", "Away", "#FFFF00"),
        new("Offline", "Off", "#000000")
    ]);

    public static StatusOption Get(string id) =>
        All.FirstOrDefault(option => option.Id == id) ??
        throw new ArgumentException("Choose a supported PresencePlus status.", nameof(id));
}
