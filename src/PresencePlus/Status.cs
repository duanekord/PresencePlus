namespace PresencePlus;

public sealed record Presence(string Availability, string Activity);
public sealed record Meeting(DateTimeOffset Start, DateTimeOffset End);
public sealed record LedState(string Status, string Color, string Effect = "solid");

public static class StatusRules
{
    public static LedState Evaluate(Presence presence, IReadOnlyList<Meeting> meetings, DateTimeOffset now)
    {
        if (presence.Activity == "Presenting")
            return new("Presenting", "#800080");
        if (presence.Availability == "DoNotDisturb")
            return new("DoNotDisturb", "#FF0000");
        if (presence.Activity is "InAMeeting" or "InACall" or "InAConferenceCall" ||
            meetings.Any(m => m.Start <= now && now < m.End))
            return new("InMeeting", "#FF0000");

        var next = meetings.Where(m => m.Start > now && m.End > m.Start)
            .Select(m => m.Start - now).DefaultIfEmpty(TimeSpan.MaxValue).Min();
        if (next <= TimeSpan.FromMinutes(1))
            return new("MeetingInOneMinute", "#FF8000", "pulse-fast");
        if (next <= TimeSpan.FromMinutes(5))
            return new("MeetingInFiveMinutes", "#FFFF00", "pulse");

        return presence.Availability switch
        {
            "Available" => new("Available", "#00FF00"),
            "Busy" => new("Busy", "#FF8000"),
            "Away" or "BeRightBack" => new("Away", "#FFFF00"),
            "Offline" => new("Offline", "#000000"),
            _ => new("Unknown", "#404040")
        };
    }
}

public sealed class OfficeState
{
    private readonly object gate = new();
    private Presence presence = new("Unknown", "Unknown");
    private IReadOnlyList<Meeting> meetings = [];

    public void Update(Presence value, IReadOnlyList<Meeting> events)
    {
        lock (gate) (presence, meetings) = (value, events);
    }

    public LedState Current(DateTimeOffset now)
    {
        lock (gate) return StatusRules.Evaluate(presence, meetings, now);
    }
}
