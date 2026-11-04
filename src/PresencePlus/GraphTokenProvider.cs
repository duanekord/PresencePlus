using System.Text.Json;

namespace PresencePlus;

public static class GraphOptions
{
    public static void Validate(IConfiguration configuration)
    {
        foreach (var name in new[] { "TenantId", "ClientId" })
            if (!Guid.TryParse(configuration[$"Graph:{name}"], out _))
                throw new InvalidOperationException($"Graph:{name} must be a GUID.");
        if (!Uri.TryCreate(configuration["Graph:NotificationUrl"], UriKind.Absolute, out var url) ||
            url.Scheme != "https" || url.AbsolutePath != "/graph/notifications" ||
            !string.IsNullOrEmpty(url.Query) || !string.IsNullOrEmpty(url.Fragment))
            throw new InvalidOperationException("Graph:NotificationUrl must be an HTTPS URL ending in /graph/notifications.");
    }
}

public sealed class GraphTokenProvider(
    IHttpClientFactory clients, IConfiguration configuration, ILogger<GraphTokenProvider> logger)
{
    private string? accessToken;
    private string? refreshToken;
    private DateTimeOffset expires;
    public int SignInVersion { get; private set; }
    private readonly string authority = $"https://login.microsoftonline.com/{configuration["Graph:TenantId"]}/oauth2/v2.0/";
    private const string Scopes = "https://graph.microsoft.com/User.Read https://graph.microsoft.com/Presence.Read.All https://graph.microsoft.com/Calendars.Read offline_access";

    public async Task<string> GetAsync(CancellationToken cancellationToken)
    {
        if (accessToken is not null && expires > DateTimeOffset.UtcNow.AddMinutes(2)) return accessToken;
        if (refreshToken is not null)
        {
            using var refresh = await PostAsync("token", new()
            {
                ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken, ["scope"] = Scopes
            }, cancellationToken);
            if (refresh.RootElement.TryGetProperty("access_token", out _)) return Save(refresh.RootElement);
            var error = refresh.RootElement.GetProperty("error").GetString();
            if (error is not ("invalid_grant" or "interaction_required"))
                throw new InvalidOperationException("Token refresh failed; check Entra configuration or service availability.");
            refreshToken = null;
        }

        using var device = await PostAsync("devicecode", new() { ["scope"] = Scopes }, cancellationToken);
        var root = device.RootElement;
        if (!root.TryGetProperty("device_code", out var code))
            throw new InvalidOperationException("Unable to start device sign-in; check Entra public-client configuration.");
        logger.LogInformation("Sign in at {Uri} using code {Code}. Tokens are kept in memory only.",
            root.GetProperty("verification_uri").GetString(), root.GetProperty("user_code").GetString());
        var deadline = DateTimeOffset.UtcNow.AddSeconds(root.GetProperty("expires_in").GetInt32());
        var interval = root.GetProperty("interval").GetInt32();
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), cancellationToken);
            using var token = await PostAsync("token", new()
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code", ["device_code"] = code.GetString()!
            }, cancellationToken);
            if (token.RootElement.TryGetProperty("access_token", out _))
            {
                SignInVersion++;
                return Save(token.RootElement);
            }
            var error = token.RootElement.GetProperty("error").GetString();
            if (error == "slow_down") interval += 5;
            else if (error != "authorization_pending")
                throw new InvalidOperationException("Device sign-in failed; retry and verify tenant policy and consent.");
        }
        throw new InvalidOperationException("Device sign-in expired; retrying.");
    }

    private string Save(JsonElement token)
    {
        accessToken = token.GetProperty("access_token").GetString()!;
        if (token.TryGetProperty("refresh_token", out var value)) refreshToken = value.GetString();
        expires = DateTimeOffset.UtcNow.AddSeconds(token.GetProperty("expires_in").GetInt32());
        return accessToken;
    }

    private async Task<JsonDocument> PostAsync(string path, Dictionary<string, string> values, CancellationToken cancellationToken)
    {
        values["client_id"] = configuration["Graph:ClientId"]!;
        using var body = new FormUrlEncodedContent(values);
        using var response = await clients.CreateClient("identity").PostAsync(authority + path, body, cancellationToken);
        // OAuth errors have structured JSON even when the response is not successful.
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    }
}
