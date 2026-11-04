using Microsoft.Identity.Client;

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

public sealed record GraphAccessToken(string AccessToken, DateTimeOffset ExpiresOn, string AccountId);

public interface IGraphAuthentication
{
    Task<GraphAccessToken> AcquireSilentAsync(CancellationToken cancellationToken);
    Task<GraphAccessToken> AcquireInteractiveAsync(string? claims, CancellationToken cancellationToken);
}

public sealed class GraphTokenProvider(
    IGraphAuthentication authentication, TimeProvider clock, ILogger<GraphTokenProvider> logger)
{
    private string? accessToken;
    private string? accountId;
    private DateTimeOffset expires;
    private DateTimeOffset interactiveRetryAt;
    public int SignInVersion { get; private set; }

    public async Task<string> GetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (accessToken is not null && expires > clock.GetUtcNow().AddMinutes(2)) return accessToken;
        try
        {
            GraphAccessToken token;
            var interactive = false;
            try
            {
                token = await authentication.AcquireSilentAsync(cancellationToken);
            }
            catch (MsalUiRequiredException exception)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (clock.GetUtcNow() < interactiveRetryAt)
                    throw new InvalidOperationException("Interactive sign-in is paused after a failed attempt; retrying within five minutes.");
                logger.LogInformation("PresencePlus needs Microsoft sign-in. Complete the Windows account prompt in your logged-in session.");
                interactiveRetryAt = clock.GetUtcNow().AddMinutes(5);
                token = await authentication.AcquireInteractiveAsync(exception.Claims, cancellationToken);
                interactive = true;
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(token.AccessToken) || string.IsNullOrWhiteSpace(token.AccountId) ||
                token.ExpiresOn <= clock.GetUtcNow())
                throw new InvalidOperationException("Microsoft sign-in did not return a usable token and account.");
            if (accountId != token.AccountId || interactive) SignInVersion++;
            accountId = token.AccountId;
            accessToken = token.AccessToken;
            expires = token.ExpiresOn;
            interactiveRetryAt = DateTimeOffset.MinValue;
            return accessToken;
        }
        catch (MsalException exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            logger.LogWarning("Microsoft sign-in failed ({ErrorCode}, correlation {CorrelationId}). Check the broker redirect URI, consent, tenant policy and connectivity. Interactive failures wait five minutes before prompting again.",
                exception.ErrorCode, exception.CorrelationId);
            throw new InvalidOperationException("Microsoft sign-in failed; see the authentication diagnostic in the local console.", exception);
        }
    }
}
