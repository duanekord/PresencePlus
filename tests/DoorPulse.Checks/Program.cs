using System.Text;
using System.Text.Json;
using DoorPulse;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Configuration;

var checks = 0;
void Equal<T>(T expected, T actual, string name)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{name}: expected {expected}, got {actual}");
    checks++;
}

var now = new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
var available = new Presence("Available", "Available");
LedState Evaluate(Presence p, params Meeting[] meetings) => StatusRules.Evaluate(p, meetings, now);
Meeting In(TimeSpan delay) => new(now + delay, now + delay + TimeSpan.FromMinutes(30));

foreach (var (availability, expected) in new[]
{
    ("Available", "Available"), ("Busy", "Busy"), ("DoNotDisturb", "DoNotDisturb"),
    ("Away", "Away"), ("BeRightBack", "Away"), ("Offline", "Offline"), ("PresenceUnknown", "Unknown")
})
    Equal(expected, Evaluate(new(availability, "Unknown")).Status, availability);
Equal("Presenting", Evaluate(new("DoNotDisturb", "Presenting")).Status, "presenting priority");
foreach (var activity in new[] { "InAMeeting", "InACall", "InAConferenceCall" })
    Equal("InMeeting", Evaluate(new("Busy", activity)).Status, activity);
Equal("Available", Evaluate(available, In(TimeSpan.FromMinutes(5) + TimeSpan.FromTicks(1))).Status, "before five minutes");
Equal("MeetingInFiveMinutes", Evaluate(available, In(TimeSpan.FromMinutes(5))).Status, "five minute boundary");
Equal("pulse", Evaluate(available, In(TimeSpan.FromMinutes(2))).Effect, "five minute effect");
Equal("MeetingInFiveMinutes", Evaluate(available, In(TimeSpan.FromMinutes(1) + TimeSpan.FromTicks(1))).Status, "before one minute");
Equal("MeetingInOneMinute", Evaluate(available, In(TimeSpan.FromMinutes(1))).Status, "one minute boundary");
Equal("pulse-fast", Evaluate(available, In(TimeSpan.FromSeconds(1))).Effect, "one minute effect");
Equal("InMeeting", Evaluate(available, In(TimeSpan.Zero)).Status, "meeting start");
Equal("Available", Evaluate(available, new Meeting(now.AddMinutes(-30), now)).Status, "meeting end");
Equal("Available", Evaluate(available, new Meeting(now.AddMinutes(1), now)).Status, "invalid event");
Equal("InMeeting", Evaluate(available, In(TimeSpan.FromMinutes(1)), In(TimeSpan.FromMinutes(-1))).Status, "overlap");
Equal("DoNotDisturb", Evaluate(new("DoNotDisturb", "Unknown"), In(TimeSpan.FromMinutes(1))).Status, "DND priority");
Equal("Presenting", Evaluate(new("Busy", "Presenting"), In(TimeSpan.FromMinutes(1))).Status, "presenting over reminder");
Equal("MeetingInOneMinute", Evaluate(available, In(TimeSpan.FromMinutes(4)), In(TimeSpan.FromSeconds(45))).Status, "nearest event");
Equal("#00FF00", Evaluate(available).Color, "available color");
Equal("#000000", Evaluate(new("Offline", "Offline")).Color, "offline color");

var state = new OfficeState();
Equal("Unknown", state.Current(now).Status, "startup");
state.Update(available, [In(TimeSpan.FromMinutes(5))]);
Equal("MeetingInFiveMinutes", state.Current(now).Status, "cached calendar");
Equal("MeetingInOneMinute", state.Current(now.AddMinutes(4)).Status, "local countdown");
Equal("InMeeting", state.Current(now.AddMinutes(5)).Status, "local start");
Equal("Available", state.Current(now.AddMinutes(35)).Status, "local end");
state.Update(available, []);
Equal("Available", state.Current(now).Status, "calendar deletion");

using var utc = JsonDocument.Parse("""{"dateTime":"2026-11-04T12:00:00.0000000","timeZone":"UTC"}""");
Equal(now, GraphWorker.ParseUtc(utc.RootElement), "Graph UTC time");

var notifications = new GraphNotifications();
notifications.Register("subscription", "expected-state");
Equal(true, notifications.Accept("subscription", "expected-state"), "valid subscription");
Equal(false, notifications.Accept("other", "expected-state"), "unknown subscription");
Equal(false, notifications.Accept("subscription", "wrong-state"), "invalid client state");

async Task<IResult> Receive(string body, string query = "")
{
    var context = new DefaultHttpContext();
    context.Request.QueryString = new QueryString(query);
    context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
    return await GraphNotifications.ReceiveAsync(context.Request, notifications);
}
var validation = (ContentHttpResult)await Receive("", "?validationToken=hello%2Bworld");
Equal("hello+world", validation.ResponseContent, "validation token decoded once");
Equal("text/plain", validation.ContentType, "validation content type");
Equal(true, await Receive("{") is BadRequest, "malformed JSON");
Equal(true, await Receive("{}") is BadRequest, "missing array");
Equal(true, await Receive("[]") is BadRequest, "invalid root");
Equal(true, await Receive("""{"value":[null]}""") is UnauthorizedHttpResult, "invalid item");
Equal(true, await Receive("""{"value":[{"subscriptionId":"subscription","clientState":"wrong"}]}""") is UnauthorizedHttpResult, "reject forged webhook");
Equal(false, notifications.TakeSignal(), "forged webhook not queued");
Equal(true, await Receive("""{"value":[{"subscriptionId":"subscription","clientState":"expected-state"}]}""") is Accepted, "accept webhook");
Equal(true, notifications.TakeSignal(), "valid webhook queued");
notifications.Signal();
notifications.Signal();
Equal(true, notifications.TakeSignal(), "coalesced notification");
Equal(false, notifications.TakeSignal(), "duplicate coalesced");
notifications.Remove("subscription");
Equal(false, notifications.Accept("subscription", "expected-state"), "removed subscription");

var handler = new DeviceHandler();
using var client = new HttpClient(handler);
var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
{
    ["Led:Url"] = "https://device.test/led"
}).Build();
await new WifiLed(client, configuration).SetAsync(new("Available", "#00FF00"), CancellationToken.None);
Equal(HttpMethod.Put, handler.Method, "device HTTP method");
Equal("https://device.test/led", handler.Url, "device URL");
using var payload = JsonDocument.Parse(handler.Body!);
Equal("Available", payload.RootElement.GetProperty("status").GetString(), "device status");
Equal("#00FF00", payload.RootElement.GetProperty("color").GetString(), "device color");
Equal("solid", payload.RootElement.GetProperty("effect").GetString(), "device effect");

Console.WriteLine($"All {checks} DoorPulse checks passed.");

sealed class DeviceHandler : HttpMessageHandler
{
    public HttpMethod? Method { get; private set; }
    public string? Url { get; private set; }
    public string? Body { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Method = request.Method;
        Url = request.RequestUri!.ToString();
        Body = await request.Content!.ReadAsStringAsync(cancellationToken);
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
    }
}
