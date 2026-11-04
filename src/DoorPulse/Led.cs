using System.Net.Http.Json;

namespace DoorPulse;

public interface ILedOutput
{
    Task SetAsync(LedState state, CancellationToken cancellationToken);
}

public sealed class ConsoleLed(ILogger<ConsoleLed> logger) : ILedOutput
{
    public Task SetAsync(LedState state, CancellationToken cancellationToken)
    {
        logger.LogInformation("LED: {Status} {Color} {Effect}", state.Status, state.Color, state.Effect);
        return Task.CompletedTask;
    }
}

public sealed class WifiLed(HttpClient client, IConfiguration configuration) : ILedOutput
{
    public async Task SetAsync(LedState state, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, configuration["Led:Url"]);
        request.Content = JsonContent.Create(state);
        if (configuration["Led:ApiKey"] is { Length: > 0 } key)
            request.Headers.Add("X-Api-Key", key);
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}

public sealed class LedWorker(OfficeState state, ILedOutput output, ILogger<LedWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LedState? previous = null;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        do
        {
            var current = state.Current(DateTimeOffset.UtcNow);
            if (current == previous) continue;
            try
            {
                await output.SetAsync(current, stoppingToken);
                previous = current;
            }
            catch (HttpRequestException)
            {
                logger.LogWarning("LED device unavailable; retrying on the next tick.");
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("LED device timed out; retrying on the next tick.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
