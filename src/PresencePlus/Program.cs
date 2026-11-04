using PresencePlus;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<OfficeState>();
builder.Services.AddSingleton<GraphNotifications>();
builder.Services.AddHttpClient("graph", client =>
{
    client.BaseAddress = new Uri("https://graph.microsoft.com/v1.0/");
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddHttpClient<WifiLed>(client => client.Timeout = TimeSpan.FromSeconds(5))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddSingleton<ILedOutput>(services =>
    builder.Configuration["Led:Url"] is { Length: > 0 }
        ? services.GetRequiredService<WifiLed>()
        : ActivatorUtilities.CreateInstance<ConsoleLed>(services));
builder.Services.AddHostedService<LedWorker>();
if (builder.Configuration.GetValue<bool>("Graph:Enabled"))
{
    GraphOptions.Validate(builder.Configuration);
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddSingleton<IGraphAuthentication, WindowsBrokerAuthentication>();
    builder.Services.AddSingleton<GraphTokenProvider>();
    builder.Services.AddHostedService<GraphWorker>();
}

var app = builder.Build();
if (builder.Configuration.GetValue<bool>("Demo"))
{
    if (builder.Configuration.GetValue<bool>("Graph:Enabled"))
        throw new InvalidOperationException("Demo and Graph cannot be enabled together.");
    var now = DateTimeOffset.UtcNow;
    app.Services.GetRequiredService<OfficeState>().Update(new("Available", "Available"),
        [new(now.AddMinutes(6), now.AddMinutes(7))]);
}
app.MapGet("/", (OfficeState state) => Results.Json(state.Current(DateTimeOffset.UtcNow)));
app.MapPost("/graph/notifications", GraphNotifications.ReceiveAsync);
app.Run();
