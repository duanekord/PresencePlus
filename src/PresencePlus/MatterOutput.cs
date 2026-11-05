using System.ComponentModel;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace PresencePlus;

public sealed record MatterDeviceInfo(bool Paired, string Name, int? Endpoint = null);

public sealed class MatterException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public interface IMatterOutput : ILedOutput
{
    Task<MatterDeviceInfo> InspectAsync(CancellationToken cancellationToken);
    Task<MatterDeviceInfo> PairAsync(string pairingCode, string ssid, string password, CancellationToken cancellationToken);
}

public sealed class MatterOutput(string helperDirectory, string storageDirectory) : IMatterOutput, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object processLock = new();
    private readonly CancellationTokenSource lifetime = new();
    private Process? process;
    private Task? errorReader;
    private long sequence;
    private long connectionVersion;
    private bool disposed;

    public bool IsRunning { get { lock (processLock) return process is { HasExited: false }; } }
    public long ConnectionVersion => Interlocked.Read(ref connectionVersion);

    public async Task SetAsync(LedState state, CancellationToken cancellationToken) =>
        _ = await SendAsync("set", new() { ["color"] = state.Color, ["brightnessPercent"] = state.BrightnessPercent },
            false, cancellationToken).ConfigureAwait(false);

    public Task<MatterDeviceInfo> InspectAsync(CancellationToken cancellationToken) =>
        SendAsync("inspect", [], false, cancellationToken);

    public Task<MatterDeviceInfo> PairAsync(string pairingCode, string ssid, string password, CancellationToken cancellationToken) =>
        SendAsync("pair", new() { ["pairingCode"] = pairingCode, ["ssid"] = ssid, ["password"] = password },
            true, cancellationToken);

    private async Task<MatterDeviceInfo> SendAsync(string action, Dictionary<string, object?> fields,
        bool restart, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (restart) StopChild();
            if (!IsRunning) StartChild();
            var child = process ?? throw new IOException("Matter controller did not start.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
            timeout.CancelAfter(action == "pair" ? TimeSpan.FromSeconds(180) : TimeSpan.FromSeconds(40));
            var id = ++sequence;
            fields.Add("id", id);
            fields.Add("action", action);
            await child.StandardInput.WriteLineAsync(JsonSerializer.Serialize(fields, JsonOptions).AsMemory(), timeout.Token)
                .ConfigureAwait(false);
            var line = await child.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false)
                ?? throw new IOException("Matter controller stopped before responding.");
            if (line.Length > 8192) throw new IOException("Matter controller returned an oversized response.");
            var reply = JsonSerializer.Deserialize<Reply>(line, JsonOptions)
                ?? throw new IOException("Matter controller returned an empty response.");
            if (reply.Id != id) throw new IOException("Matter controller returned an out-of-order response.");
            if (!reply.Ok) throw new MatterException(reply.Code ?? "ControllerError",
                reply.Message ?? "Matter controller could not complete the request.");
            return reply.Device ?? throw new IOException("Matter controller did not return device information.");
        }
        catch (MatterException) { throw; }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !lifetime.IsCancellationRequested)
        {
            StopChild();
            throw new MatterException("Timeout", "Matter operation timed out. Check the light's power and your local network.");
        }
        catch (OperationCanceledException)
        {
            StopChild();
            throw;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or SecurityException)
        {
            StopChild();
            throw new MatterException("StorageUnavailable",
                "The Matter controller cannot access its private files. Check your Windows account and folder permissions.");
        }
        catch (Exception exception) when (exception is IOException or JsonException or Win32Exception or NetworkInformationException)
        {
            StopChild();
            throw new MatterException("ControllerUnavailable",
                "The local Matter controller is unavailable. Check the published app files and retry.");
        }
        finally
        {
            fields.Clear();
            gate.Release();
        }
    }

    private void StartChild()
    {
        StopChild();
        var executable = Path.Combine(helperDirectory, "node.exe");
        var entrypoint = Path.Combine(helperDirectory, "dist", "main.js");
        if (!File.Exists(executable) || !File.Exists(entrypoint))
            throw new MatterException("NotInstalled", @"Matter runtime is missing. Build the complete app with scripts\Publish.ps1.");
        EnsurePrivateStorage(storageDirectory);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            WorkingDirectory = helperDirectory
        };
        start.ArgumentList.Add(entrypoint);
        start.ArgumentList.Add("--presenceplus-data-dir");
        start.ArgumentList.Add(Path.GetFullPath(storageDirectory));
        var network = NetworkInterface.GetAllNetworkInterfaces()
            .Where(item => item.OperationalStatus == OperationalStatus.Up && item.SupportsMulticast &&
                item.NetworkInterfaceType is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet &&
                item.GetIPProperties().GatewayAddresses.Count > 0)
            .OrderBy(item => item.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 0 : 1)
            .FirstOrDefault();
        if (network is not null) start.ArgumentList.Add($"--mdns-networkinterface={network.Name}");
        var child = Process.Start(start) ?? throw new IOException("Could not launch the local Matter controller.");
        lock (processLock) process = child;
        Interlocked.Increment(ref connectionVersion);
        errorReader = DrainErrorsAsync(child);
    }

    private static async Task DrainErrorsAsync(Process child)
    {
        try
        {
            while (await child.StandardError.ReadLineAsync().ConfigureAwait(false) is not null) { }
        }
        catch (ObjectDisposedException) { /* The owning process was disposed during shutdown. */ }
        catch (IOException) when (child.HasExited) { }
    }

    private void StopChild()
    {
        Process? child;
        lock (processLock) { child = process; process = null; }
        if (child is null) return;
        try
        {
            if (!child.HasExited)
            {
                child.StandardInput.Close();
                if (!child.WaitForExit(3000))
                {
                    child.Kill(entireProcessTree: true);
                    child.WaitForExit(3000);
                }
            }
            errorReader?.GetAwaiter().GetResult();
        }
        finally
        {
            child.Dispose();
            errorReader = null;
        }
    }

    public static void EnsurePrivateStorage(string path)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(path));
        if (directory.Exists && directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("The Matter storage directory must not be a link.");
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User ?? throw new IOException("The current Windows user cannot be identified.");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        directory.Create(security);
        directory.SetAccessControl(security);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        gate.Wait();
        try { StopChild(); }
        finally { gate.Release(); lifetime.Dispose(); }
    }

    private sealed record Reply(long Id, bool Ok, MatterDeviceInfo? Device, string? Code, string? Message);
}
