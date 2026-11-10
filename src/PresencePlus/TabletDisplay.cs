using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace PresencePlus;

public sealed record TabletStatus(string Status, string Name, string Color);

public sealed class TabletDisplay : IDisposable
{
    public const int DefaultPort = 47831;
    private readonly IPAddress address;
    private readonly int port;
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private bool disposed;
    private WebApplication? application;
    private TabletStatus status;
    private byte[] key;
    private int listeningPort;

    public TabletDisplay(AppSettings settings) : this(settings, IPAddress.Any, DefaultPort) { }

    internal TabletDisplay(AppSettings settings, IPAddress address, int port)
    {
        this.address = address;
        this.port = port;
        status = Snapshot(settings);
        key = Encoding.ASCII.GetBytes(settings.TabletDisplayKey ?? AppSettings.CreateTabletDisplayKey());
    }

    public bool IsRunning => Volatile.Read(ref application) is not null;

    public void Update(AppSettings settings)
    {
        Volatile.Write(ref status, Snapshot(settings));
        if (settings.TabletDisplayKey is not null)
            Volatile.Write(ref key, Encoding.ASCII.GetBytes(settings.TabletDisplayKey));
    }

    private static TabletStatus Snapshot(AppSettings settings)
    {
        settings.Validate();
        return new(settings.Status, StatusOptions.Get(settings.Status).Label, settings.ColorFor(settings.Status));
    }

    public async Task StartAsync()
    {
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try { await StartCoreAsync().ConfigureAwait(false); }
        finally { lifecycle.Release(); }
    }

    private async Task StartCoreAsync()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (application is not null) return;
        var assets = new Dictionary<string, (string Content, string Type)>
        {
            ["/"] = (ReadAsset("index.html"), "text/html; charset=utf-8"),
            ["/display.css"] = (ReadAsset("display.css"), "text/css; charset=utf-8"),
            ["/display.js"] = (ReadAsset("display.js"), "text/javascript; charset=utf-8")
        };
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [], ContentRootPath = AppContext.BaseDirectory,
            ApplicationName = typeof(TabletDisplay).Assembly.GetName().Name
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Listen(address, port);
            options.Limits.MaxConcurrentConnections = 32;
            options.Limits.MaxRequestBodySize = 0;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5);
            options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(10);
        });
        var next = builder.Build();
        next.Run(async context =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers.ContentSecurityPolicy =
                "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";
            if (!IsLocalAddress(context.Connection.RemoteIpAddress) ||
                !IPAddress.TryParse(context.Request.Host.Host, out var host) || !IsLocalAddress(host))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            if (!HttpMethods.IsGet(context.Request.Method))
            {
                context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                context.Response.Headers.Allow = "GET";
                return;
            }
            if (context.Request.Path == "/api/status")
            {
                var supplied = Encoding.ASCII.GetBytes(context.Request.Headers["X-PresencePlus-Display-Key"].ToString());
                if (!CryptographicOperations.FixedTimeEquals(Volatile.Read(ref key), supplied))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }
                await context.Response.WriteAsJsonAsync(Volatile.Read(ref status), context.RequestAborted);
                return;
            }
            if (assets.TryGetValue(context.Request.Path.Value ?? "", out var asset))
            {
                context.Response.ContentType = asset.Type;
                await context.Response.WriteAsync(asset.Content, context.RequestAborted);
                return;
            }
            context.Response.StatusCode = StatusCodes.Status404NotFound;
        });
        try
        {
            await next.StartAsync().ConfigureAwait(false);
            var addresses = next.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
                ?? throw new InvalidOperationException("The tablet display did not report a listening address.");
            listeningPort = new Uri(addresses.Addresses.Single()).Port;
            application = next;
        }
        catch
        {
            await next.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public IReadOnlyList<string> GetLinks()
    {
        if (!IsRunning) throw new InvalidOperationException("Enable the tablet display first.");
        var addresses = address.Equals(IPAddress.Any)
            ? NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up &&
                    adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
                .Select(item => item.Address)
                .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork && IsLocalAddress(ip))
                .Distinct().OrderBy(ip => ip.ToString()).ToArray()
            : [address];
        var currentKey = Encoding.ASCII.GetString(Volatile.Read(ref key));
        return addresses.Select(ip => $"http://{ip}:{listeningPort}/#{currentKey}").ToArray();
    }

    internal static bool IsLocalAddress(IPAddress? address)
    {
        if (address is null) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        var bytes = address.GetAddressBytes();
        return address.AddressFamily == AddressFamily.InterNetwork &&
            (bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
             bytes[0] == 192 && bytes[1] == 168 || bytes[0] == 169 && bytes[1] == 254);
    }

    private static string ReadAsset(string name)
    {
        using var stream = typeof(TabletDisplay).Assembly.GetManifestResourceStream($"PresencePlus.TabletDisplay.{name}")
            ?? throw new InvalidOperationException($"The tablet display asset {name} is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public async Task StopAsync()
    {
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try { await StopCoreAsync().ConfigureAwait(false); }
        finally { lifecycle.Release(); }
    }

    private async Task StopCoreAsync()
    {
        var previous = application;
        if (previous is null) return;
        await previous.DisposeAsync().ConfigureAwait(false);
        application = null;
    }

    public void Dispose() => Task.Run(async () =>
    {
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            disposed = true;
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally { lifecycle.Release(); }
    }).GetAwaiter().GetResult();
}
