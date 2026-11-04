using System.Text;
using System.Text.Json;
using PresencePlus;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Identity.Client;

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
Equal(true, await Receive("""{"value":[{"subscriptionId":"subscription","clientState":"expected-state"},{"subscriptionId":"stale","clientState":"stale"}]}""") is Accepted, "mixed notification batch");
Equal(true, notifications.TakeSignal(), "valid entry survives stale entry");
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

async Task<TException> ThrowsAsync<TException>(Func<Task> action, string name) where TException : Exception
{
    try { await action(); }
    catch (TException exception)
    {
        checks++;
        return exception;
    }
    throw new InvalidOperationException($"{name}: expected {typeof(TException).Name}");
}

var clock = new CheckClock(now);
GraphTokenProvider Provider(CheckAuthentication authentication) =>
    new(authentication, clock, NullLogger<GraphTokenProvider>.Instance);
GraphAccessToken Token(string access = "first-access", string account = "tenant/account", int minutes = 60) =>
    new(access, clock.GetUtcNow().AddMinutes(minutes), account);
Task<GraphAccessToken> NeedsInteraction(CancellationToken _) =>
    Task.FromException<GraphAccessToken>(new MsalUiRequiredException("interaction_required", "Test sign-in required."));

var silent = new CheckAuthentication();
silent.Silent.Enqueue(_ => Task.FromResult(Token(minutes: 3)));
silent.Silent.Enqueue(_ => Task.FromResult(Token("refreshed-access")));
silent.Silent.Enqueue(_ => Task.FromResult(Token("other-access", "tenant/other-account")));
var tokenProvider = Provider(silent);
Equal("first-access", await tokenProvider.GetAsync(CancellationToken.None), "silent startup");
Equal(1, tokenProvider.SignInVersion, "silent startup initializes identity");
Equal("first-access", await tokenProvider.GetAsync(CancellationToken.None), "cached access token");
Equal(1, silent.SilentCalls, "cached token avoids broker calls");
clock.Advance(TimeSpan.FromMinutes(1));
Equal("refreshed-access", await tokenProvider.GetAsync(CancellationToken.None), "refresh at two-minute boundary");
Equal(1, tokenProvider.SignInVersion, "silent refresh preserves identity");
clock.Advance(TimeSpan.FromHours(1));
Equal("other-access", await tokenProvider.GetAsync(CancellationToken.None), "silent account change");
Equal(2, tokenProvider.SignInVersion, "silent account change invalidates Graph identity");
Equal(0, silent.InteractiveCalls, "silent success never opens sign-in UI");

var interactive = new CheckAuthentication();
interactive.Silent.Enqueue(NeedsInteraction);
interactive.Interactive.Enqueue(_ => Task.FromResult(Token(minutes: 3)));
interactive.Silent.Enqueue(NeedsInteraction);
interactive.Interactive.Enqueue(_ => Task.FromResult(Token("second-access")));
tokenProvider = Provider(interactive);
Equal("first-access", await tokenProvider.GetAsync(CancellationToken.None), "broker interactive fallback");
Equal(1, tokenProvider.SignInVersion, "initial interactive account version");
Equal(1, interactive.InteractiveCalls, "interactive fallback only when required");
clock.Advance(TimeSpan.FromMinutes(1));
Equal("second-access", await tokenProvider.GetAsync(CancellationToken.None), "broker reauthentication");
Equal(2, tokenProvider.SignInVersion, "reauthentication invalidates cached identity");
Equal("second-access", await tokenProvider.GetAsync(CancellationToken.None), "cached interactive access token");
Equal(2, tokenProvider.SignInVersion, "cached access preserves identity version");

var restarted = new CheckAuthentication();
restarted.Silent.Enqueue(_ => Task.FromResult(Token("restart-access")));
Equal("restart-access", await Provider(restarted).GetAsync(CancellationToken.None), "restart can reuse broker sign-in silently");
Equal(0, restarted.InteractiveCalls, "restart does not force interactive sign-in");

var failing = new CheckAuthentication();
failing.Silent.Enqueue(_ => Task.FromException<GraphAccessToken>(new MsalServiceException("temporarily_unavailable", "Test outage.")));
tokenProvider = Provider(failing);
var failure = await ThrowsAsync<InvalidOperationException>(() => tokenProvider.GetAsync(CancellationToken.None), "broker errors surface");
Equal(true, failure.InnerException is MsalServiceException, "preserve broker error for diagnostics");
Equal(0, failing.InteractiveCalls, "network errors do not trigger interactive sign-in");
Equal(0, tokenProvider.SignInVersion, "failed authentication preserves identity version");

var canceledPrompt = new CheckAuthentication();
canceledPrompt.Silent.Enqueue(NeedsInteraction);
canceledPrompt.Silent.Enqueue(NeedsInteraction);
canceledPrompt.Silent.Enqueue(NeedsInteraction);
canceledPrompt.Interactive.Enqueue(_ => Task.FromException<GraphAccessToken>(new MsalClientException("authentication_canceled", "Test cancellation.")));
canceledPrompt.Interactive.Enqueue(_ => Task.FromResult(Token("retry-access")));
tokenProvider = Provider(canceledPrompt);
await ThrowsAsync<InvalidOperationException>(() => tokenProvider.GetAsync(CancellationToken.None), "canceled broker prompt surfaces");
await ThrowsAsync<InvalidOperationException>(() => tokenProvider.GetAsync(CancellationToken.None), "failed prompt cooldown");
Equal(1, canceledPrompt.InteractiveCalls, "failed prompt does not reopen on every Graph retry");
clock.Advance(TimeSpan.FromMinutes(5));
Equal("retry-access", await tokenProvider.GetAsync(CancellationToken.None), "interactive retry after cooldown");
Equal(2, canceledPrompt.InteractiveCalls, "prompt resumes after cooldown");
Equal(1, tokenProvider.SignInVersion, "only successful interactive sign-in updates identity");

var invalid = new CheckAuthentication();
invalid.Silent.Enqueue(_ => Task.FromResult(Token(access: "")));
invalid.Silent.Enqueue(_ => Task.FromResult(Token(account: "")));
invalid.Silent.Enqueue(_ => Task.FromResult(Token(minutes: 0)));
tokenProvider = Provider(invalid);
await ThrowsAsync<InvalidOperationException>(() => tokenProvider.GetAsync(CancellationToken.None), "reject empty access token");
await ThrowsAsync<InvalidOperationException>(() => tokenProvider.GetAsync(CancellationToken.None), "reject missing account");
await ThrowsAsync<InvalidOperationException>(() => tokenProvider.GetAsync(CancellationToken.None), "reject expired token");
Equal(0, tokenProvider.SignInVersion, "invalid token never updates identity");

using var canceled = new CancellationTokenSource();
canceled.Cancel();
await ThrowsAsync<OperationCanceledException>(() => tokenProvider.GetAsync(canceled.Token), "pre-canceled request");
Equal(3, invalid.SilentCalls, "pre-canceled request never contacts broker");
var cancellation = new CheckAuthentication();
cancellation.Silent.Enqueue(token => Task.FromCanceled<GraphAccessToken>(new CancellationToken(true)));
tokenProvider = Provider(cancellation);
await ThrowsAsync<OperationCanceledException>(() => tokenProvider.GetAsync(CancellationToken.None), "silent cancellation propagates");
Equal(0, cancellation.InteractiveCalls, "silent cancellation never opens UI");

using var forwardedCancellation = new CancellationTokenSource();
var forwarding = new CheckAuthentication();
forwarding.Silent.Enqueue(token =>
{
    Equal(forwardedCancellation.Token, token, "silent cancellation token forwarded");
    return NeedsInteraction(token);
});
forwarding.Interactive.Enqueue(token =>
{
    Equal(forwardedCancellation.Token, token, "interactive cancellation token forwarded");
    return Task.FromCanceled<GraphAccessToken>(new CancellationToken(true));
});
await ThrowsAsync<OperationCanceledException>(() => Provider(forwarding).GetAsync(forwardedCancellation.Token), "interactive cancellation propagates");

var graphConfiguration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
{
    ["Graph:TenantId"] = "11111111-1111-1111-1111-111111111111",
    ["Graph:ClientId"] = "22222222-2222-2222-2222-222222222222",
    ["Graph:NotificationUrl"] = "https://example.test/graph/notifications"
}).Build();
GraphOptions.Validate(graphConfiguration);
if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 15063) && Environment.UserInteractive)
{
    _ = new WindowsBrokerAuthentication(graphConfiguration);
    checks++;
}

Console.WriteLine($"All {checks} PresencePlus checks passed.");

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

sealed class CheckClock(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset current = now;
    public override DateTimeOffset GetUtcNow() => current;
    public void Advance(TimeSpan amount) => current += amount;
}

sealed class CheckAuthentication : IGraphAuthentication
{
    public Queue<Func<CancellationToken, Task<GraphAccessToken>>> Silent { get; } = new();
    public Queue<Func<CancellationToken, Task<GraphAccessToken>>> Interactive { get; } = new();
    public int SilentCalls { get; private set; }
    public int InteractiveCalls { get; private set; }

    public Task<GraphAccessToken> AcquireSilentAsync(CancellationToken cancellationToken)
    {
        SilentCalls++;
        return Silent.Dequeue()(cancellationToken);
    }

    public Task<GraphAccessToken> AcquireInteractiveAsync(string? claims, CancellationToken cancellationToken)
    {
        InteractiveCalls++;
        return Interactive.Dequeue()(cancellationToken);
    }
}
