using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using PresencePlus;

internal static class TabletDisplayChecks
{
    public static async Task<int> RunAsync()
    {
        var checks = 0;
        void Equal<T>(T expected, T actual, string name)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new InvalidOperationException($"{name}: expected {expected}, got {actual}");
            checks++;
        }

        foreach (var ip in new[] { "127.0.0.1", "::1", "10.1.2.3", "172.16.0.1", "172.31.255.254", "192.168.1.2", "169.254.1.2", "::ffff:192.168.1.2" })
            Equal(true, TabletDisplay.IsLocalAddress(IPAddress.Parse(ip)), "private client accepted");
        foreach (var ip in new[] { "8.8.8.8", "172.15.0.1", "172.32.0.1", "192.169.1.2", "0.0.0.0", "224.0.0.1", "2001:4860:4860::8888" })
            Equal(false, TabletDisplay.IsLocalAddress(IPAddress.Parse(ip)), "nonlocal client rejected");
        Equal(false, TabletDisplay.IsLocalAddress(null), "missing client rejected");

        var savedSettings = new AppSettings().WithTabletSharing(true);
        using var display = new TabletDisplay(savedSettings, IPAddress.Loopback, 0);
        Equal(false, display.IsRunning, "sharing is opt-in");
        await display.StartAsync();
        Equal(true, display.IsRunning, "host started");
        var link = new Uri(display.GetLinks().Single());
        var key = link.Fragment[1..];
        Equal(64, key.Length, "unguessable viewing key");
        using var client = new HttpClient { BaseAddress = new Uri(link.GetLeftPart(UriPartial.Authority)), Timeout = TimeSpan.FromSeconds(5) };
        using (var response = await client.GetAsync("/api/status"))
            Equal(HttpStatusCode.Unauthorized, response.StatusCode, "status requires viewing key");
        client.DefaultRequestHeaders.Add("X-PresencePlus-Display-Key", new string('0', 64));
        using (var response = await client.GetAsync("/api/status"))
            Equal(HttpStatusCode.Unauthorized, response.StatusCode, "wrong viewing key rejected");
        client.DefaultRequestHeaders.Remove("X-PresencePlus-Display-Key");
        client.DefaultRequestHeaders.Add("X-PresencePlus-Display-Key", key);
        foreach (var option in StatusOptions.All)
        {
            var settings = new AppSettings { Status = option.Id };
            if (option.Id != "Offline") settings = settings.WithStatusColor(option.Id, "#123ABC");
            display.Update(settings);
            using var response = await client.GetAsync("/api/status");
            Equal(HttpStatusCode.OK, response.StatusCode, "status served without Matter pairing");
            Equal(true, response.Headers.CacheControl?.NoStore == true, "status is never cached");
            Equal(false, response.Headers.Contains("Access-Control-Allow-Origin"), "no cross-origin status access");
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Equal(3, json.RootElement.EnumerateObject().Count(), "only display fields exposed");
            Equal(option.Id, json.RootElement.GetProperty("status").GetString(), "latest status id");
            Equal(option.Label, json.RootElement.GetProperty("name").GetString(), "display label matches Windows");
            Equal(settings.ColorFor(option.Id), json.RootElement.GetProperty("color").GetString(), "custom color and Off match Windows");
        }
        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Patch, HttpMethod.Delete, HttpMethod.Options })
        {
            using var request = new HttpRequestMessage(method, "/api/status");
            using var response = await client.SendAsync(request);
            Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode, "viewer cannot change status");
        }
        foreach (var path in new[] { "/", "/display.js", "/display.css" })
        {
            using var response = await client.GetAsync(path);
            Equal(HttpStatusCode.OK, response.StatusCode, "embedded viewer asset available");
            Equal(true, response.Headers.Contains("Content-Security-Policy"), "viewer has content security policy");
            Equal(true, response.Headers.CacheControl?.NoStore == true, "assets never cached across app restarts");
            Equal(false, (await response.Content.ReadAsStringAsync()).Contains(key), "viewer assets do not expose viewing key");
        }
        using (var response = await client.GetAsync("/settings.json"))
            Equal(HttpStatusCode.NotFound, response.StatusCode, "no arbitrary file server");
        using (var request = new HttpRequestMessage(HttpMethod.Get, "/api/status"))
        {
            request.Headers.Host = "untrusted.example";
            using var response = await client.SendAsync(request);
            Equal(HttpStatusCode.Forbidden, response.StatusCode, "DNS hostnames rejected");
        }
        await display.StartAsync();
        Equal(link.ToString(), display.GetLinks().Single(), "repeated enable preserves current link");
        await display.StopAsync();
        Equal(false, display.IsRunning, "disable stops listener");
        try
        {
            await client.GetAsync("/api/status");
            throw new InvalidOperationException("Disabled viewer still responds.");
        }
        catch (HttpRequestException) { checks++; }
        await display.StartAsync();
        var replacement = new Uri(display.GetLinks().Single());
        Equal(key, replacement.Fragment[1..], "re-enable preserves viewing key");
        using (var staleClient = new HttpClient())
        {
            staleClient.DefaultRequestHeaders.Add("X-PresencePlus-Display-Key", key);
            using var response = await staleClient.GetAsync(new Uri(replacement, "/api/status"));
            Equal(HttpStatusCode.OK, response.StatusCode, "bookmarked key still works after re-enable");
        }
        var resetSettings = savedSettings.WithNewTabletDisplayKey();
        display.Update(resetSettings);
        var resetLink = new Uri(display.GetLinks().Single());
        Equal(false, key == resetLink.Fragment[1..], "explicit reset rotates viewing key");
        using (var oldClient = new HttpClient())
        {
            oldClient.DefaultRequestHeaders.Add("X-PresencePlus-Display-Key", key);
            using var response = await oldClient.GetAsync(new Uri(resetLink, "/api/status"));
            Equal(HttpStatusCode.Unauthorized, response.StatusCode, "reset immediately revokes old link");
        }
        using (var newClient = new HttpClient())
        {
            newClient.DefaultRequestHeaders.Add("X-PresencePlus-Display-Key", resetLink.Fragment[1..]);
            using var response = await newClient.GetAsync(new Uri(resetLink, "/api/status"));
            Equal(HttpStatusCode.OK, response.StatusCode, "replacement link works without restarting host");
        }
        display.Dispose();
        Equal(false, display.IsRunning, "dispose stops listener");
        try
        {
            await display.StartAsync();
            throw new InvalidOperationException("Disposed display restarted.");
        }
        catch (ObjectDisposedException) { checks++; }

        using (var restarted = new TabletDisplay(resetSettings, IPAddress.Loopback, resetLink.Port))
        {
            await restarted.StartAsync();
            Equal(resetLink.ToString(), restarted.GetLinks().Single(), "entire URL survives recreation on the same address and port");
            using var restartedClient = new HttpClient();
            restartedClient.DefaultRequestHeaders.Add("X-PresencePlus-Display-Key", resetLink.Fragment[1..]);
            using var response = await restartedClient.GetAsync(new Uri(resetLink, "/api/status"));
            Equal(HttpStatusCode.OK, response.StatusCode, "saved bookmark authenticates after app recreation");
        }

        using (var racing = new TabletDisplay(new AppSettings(), IPAddress.Loopback, 0))
        {
            await Task.WhenAll(racing.StartAsync(), racing.StopAsync());
            Equal(false, racing.IsRunning, "stop during startup cannot leave a listener behind");
            var starting = racing.StartAsync();
            racing.Dispose();
            await starting;
            Equal(false, racing.IsRunning, "dispose during startup cannot leave a listener behind");
        }

        using var reserved = new TcpListener(IPAddress.Loopback, 0);
        reserved.Start();
        using var conflicting = new TabletDisplay(new AppSettings(), IPAddress.Loopback, ((IPEndPoint)reserved.LocalEndpoint).Port);
        try
        {
            await conflicting.StartAsync();
            throw new InvalidOperationException("Occupied display port was silently accepted.");
        }
        catch (IOException) { checks++; }
        Equal(false, conflicting.IsRunning, "failed start never appears enabled");
        return checks;
    }
}
