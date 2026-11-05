namespace PresencePlus;

public interface ILedOutput
{
    bool IsRunning { get; }
    long ConnectionVersion { get; }
    Task SetAsync(LedState state, CancellationToken cancellationToken);
}

public enum LightConnectionState { Connecting, Connected, Updating, Retrying, NotPaired, Unavailable }

public sealed class LedController(ILedOutput output, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private AppSettings settings = new();
    private long revision;
    private long deliveredRevision = -1;
    private long deliveredConnection = -1;
    private DateTimeOffset deliveredAt = DateTimeOffset.MinValue;

    public void Configure(AppSettings value)
    {
        value.Validate();
        var changed = settings.GetLedState() != value.GetLedState();
        settings = value;
        if (changed) revision++;
    }

    public void Retry() => deliveredRevision = -1;

    public async Task<LightConnectionState> SynchronizeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var current = settings;
        var sendingRevision = revision;
        // Periodic reassertion detects device restarts even when no status changes.
        if (deliveredRevision == sendingRevision && output.IsRunning &&
            deliveredConnection == output.ConnectionVersion && clock.GetUtcNow() - deliveredAt < TimeSpan.FromSeconds(30))
            return LightConnectionState.Connected;

        await output.SetAsync(current.GetLedState(), cancellationToken);
        deliveredRevision = sendingRevision;
        deliveredConnection = output.ConnectionVersion;
        deliveredAt = clock.GetUtcNow();
        return sendingRevision == revision
            ? LightConnectionState.Connected
            : LightConnectionState.Updating;
    }
}
