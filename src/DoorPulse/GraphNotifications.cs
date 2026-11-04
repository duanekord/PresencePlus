using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace DoorPulse;

public sealed class GraphNotifications
{
    private readonly ConcurrentDictionary<string, byte[]> subscriptions = new();
    private readonly Channel<bool> changes = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public void Register(string id, string clientState) =>
        subscriptions[id] = SHA256.HashData(Encoding.UTF8.GetBytes(clientState));
    public void Remove(string id) => subscriptions.TryRemove(id, out _);
    public void Signal() => changes.Writer.TryWrite(true);
    public bool TakeSignal() => changes.Reader.TryRead(out _);

    public bool Accept(string id, string clientState) =>
        subscriptions.TryGetValue(id, out var expected) &&
        CryptographicOperations.FixedTimeEquals(expected, SHA256.HashData(Encoding.UTF8.GetBytes(clientState)));

    public static async Task<IResult> ReceiveAsync(HttpRequest request, GraphNotifications notifications)
    {
        if (request.Query.TryGetValue("validationToken", out var token))
            return Results.Text(token.ToString(), "text/plain");
        try
        {
            using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: request.HttpContext.RequestAborted);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("value", out var items) || items.ValueKind != JsonValueKind.Array)
                return Results.BadRequest();
            var accepted = false;
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("subscriptionId", out var id) || id.ValueKind != JsonValueKind.String ||
                    !item.TryGetProperty("clientState", out var secret) || secret.ValueKind != JsonValueKind.String ||
                    !notifications.Accept(id.GetString()!, secret.GetString()!))
                    return Results.Unauthorized();
                accepted = true;
            }
            if (accepted) notifications.Signal();
            return Results.Accepted();
        }
        catch (JsonException)
        {
            return Results.BadRequest();
        }
    }
}
