using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace PresencePlus;

public sealed class GraphWorker(
    IHttpClientFactory clients, GraphTokenProvider tokens, GraphNotifications notifications,
    OfficeState state, IConfiguration configuration, ILogger<GraphWorker> logger) : BackgroundService
{
    private sealed record Subscription(string Id, DateTimeOffset Expires);
    private readonly Dictionary<string, Subscription> subscriptions = [];
    private string? userId;
    private bool previousSubscriptionsRemoved;
    private int signInVersion;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var dirty = true;
        var reconcileAt = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var client = clients.CreateClient("graph");
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetAsync(stoppingToken));
                if (userId is null || signInVersion != tokens.SignInVersion)
                {
                    using var me = await GetAsync(client, "me?$select=id", stoppingToken);
                    userId = Guid.Parse(me.RootElement.GetProperty("id").GetString()!).ToString();
                    signInVersion = tokens.SignInVersion;
                    foreach (var subscription in subscriptions.Values) notifications.Remove(subscription.Id);
                    subscriptions.Clear();
                    previousSubscriptionsRemoved = false;
                    state.Update(new("Unknown", "Unknown"), []);
                }
                if (!previousSubscriptionsRemoved)
                {
                    await RemovePreviousSubscriptionsAsync(client, stoppingToken);
                    previousSubscriptionsRemoved = true;
                }
                var renewed = await EnsureSubscriptionsAsync(client, stoppingToken);
                dirty |= notifications.TakeSignal() || renewed || DateTimeOffset.UtcNow >= reconcileAt;
                if (dirty)
                {
                    await RefreshAsync(client, stoppingToken);
                    dirty = false;
                    reconcileAt = DateTimeOffset.UtcNow.AddHours(6);
                }
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) when (exception is HttpRequestException or JsonException or
                InvalidOperationException or FormatException or OperationCanceledException)
            {
                dirty = true;
                logger.LogWarning("Graph synchronization failed ({ErrorType}); retrying in 30 seconds. Check sign-in, permissions and connectivity.",
                    exception.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }
    }

    private async Task RemovePreviousSubscriptionsAsync(HttpClient client, CancellationToken cancellationToken)
    {
        // A single instance owns subscriptions for this app and callback URL.
        using var existing = await GetAsync(client, "subscriptions", cancellationToken);
        foreach (var item in existing.RootElement.GetProperty("value").EnumerateArray())
        {
            var resource = item.GetProperty("resource").GetString();
            if (item.GetProperty("notificationUrl").GetString() == configuration["Graph:NotificationUrl"] &&
                (resource == $"communications/presences/{userId}" || resource == $"users/{userId}/events"))
            {
                using var response = await client.DeleteAsync($"subscriptions/{Uri.EscapeDataString(item.GetProperty("id").GetString()!)}", cancellationToken);
                if (response.StatusCode != HttpStatusCode.NotFound) response.EnsureSuccessStatusCode();
            }
        }
    }

    private async Task<bool> EnsureSubscriptionsAsync(HttpClient client, CancellationToken cancellationToken)
    {
        var changed = false;
        foreach (var resource in new[] { $"communications/presences/{userId}", $"users/{userId}/events" })
        {
            var expiration = DateTimeOffset.UtcNow.AddMinutes(45);
            if (subscriptions.TryGetValue(resource, out var subscription))
            {
                if (subscription.Expires > DateTimeOffset.UtcNow.AddMinutes(10)) continue;
                using var renewal = await client.PatchAsJsonAsync($"subscriptions/{Uri.EscapeDataString(subscription.Id)}",
                    new { expirationDateTime = expiration }, cancellationToken);
                if (renewal.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                {
                    notifications.Remove(subscription.Id);
                    subscriptions.Remove(resource);
                }
                else
                {
                    renewal.EnsureSuccessStatusCode();
                    using var renewed = JsonDocument.Parse(await renewal.Content.ReadAsStringAsync(cancellationToken));
                    subscriptions[resource] = new(subscription.Id, renewed.RootElement.GetProperty("expirationDateTime").GetDateTimeOffset());
                    changed = true;
                    continue;
                }
            }
            var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            using var response = await client.PostAsJsonAsync("subscriptions", new
            {
                changeType = resource.StartsWith("communications/", StringComparison.Ordinal) ? "updated" : "created,updated,deleted",
                notificationUrl = configuration["Graph:NotificationUrl"], resource,
                expirationDateTime = expiration, clientState = secret, includeResourceData = false
            }, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var created = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var id = created.RootElement.GetProperty("id").GetString()!;
            notifications.Register(id, secret);
            subscriptions[resource] = new(id, created.RootElement.GetProperty("expirationDateTime").GetDateTimeOffset());
            changed = true;
        }
        return changed;
    }

    private async Task RefreshAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using var presence = await GetAsync(client, $"users/{userId}/presence", cancellationToken);
        var now = DateTimeOffset.UtcNow;
        string? path = $"users/{userId}/calendarView?startDateTime={Uri.EscapeDataString(now.ToString("O"))}" +
            $"&endDateTime={Uri.EscapeDataString(now.AddDays(2).ToString("O"))}" +
            "&$select=start,end,isCancelled,showAs,responseStatus&$top=100";
        var meetings = new List<Meeting>();
        while (path is not null)
        {
            using var page = await GetAsync(client, path, cancellationToken);
            foreach (var item in page.RootElement.GetProperty("value").EnumerateArray())
            {
                if (item.GetProperty("isCancelled").GetBoolean() ||
                    item.GetProperty("showAs").GetString() is not ("busy" or "oof") ||
                    item.GetProperty("responseStatus").GetProperty("response").GetString() == "declined")
                    continue;
                meetings.Add(new(ParseUtc(item.GetProperty("start")), ParseUtc(item.GetProperty("end"))));
            }
            path = page.RootElement.TryGetProperty("@odata.nextLink", out var next) ? next.GetString() : null;
            if (path is not null && (!Uri.TryCreate(path, UriKind.Absolute, out var uri) ||
                uri.Scheme != "https" || uri.Host != "graph.microsoft.com" || !uri.IsDefaultPort))
                throw new InvalidOperationException("Unexpected Graph pagination URL.");
        }
        state.Update(new(presence.RootElement.GetProperty("availability").GetString()!,
            presence.RootElement.GetProperty("activity").GetString()!), meetings);
    }

    public static DateTimeOffset ParseUtc(JsonElement value)
    {
        if (value.GetProperty("timeZone").GetString() != "UTC")
            throw new InvalidOperationException("Graph did not return the requested UTC calendar times.");
        return DateTimeOffset.Parse(value.GetProperty("dateTime").GetString()!, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    }

    private static async Task<JsonDocument> GetAsync(HttpClient client, string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Prefer", "outlook.timezone=\"UTC\"");
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    }
}
