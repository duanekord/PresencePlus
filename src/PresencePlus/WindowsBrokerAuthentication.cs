using System.Runtime.InteropServices;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Broker;

namespace PresencePlus;

public sealed class WindowsBrokerAuthentication : IGraphAuthentication
{
    // MSAL adds the OpenID Connect scopes, including offline_access, automatically.
    private static readonly string[] Scopes =
    [
        "https://graph.microsoft.com/User.Read",
        "https://graph.microsoft.com/Presence.Read.All",
        "https://graph.microsoft.com/Calendars.Read"
    ];
    private readonly IPublicClientApplication application;
    private IAccount? account;

    public WindowsBrokerAuthentication(IConfiguration configuration)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 15063) || !Environment.UserInteractive)
            throw new PlatformNotSupportedException("Graph sign-in requires Windows 10 version 1703 or later and an interactive user session. Run PresencePlus as your logged-in user, not as a Windows service.");
        application = PublicClientApplicationBuilder.Create(configuration["Graph:ClientId"])
            .WithAuthority(AzureCloudInstance.AzurePublic, configuration["Graph:TenantId"])
            .WithDefaultRedirectUri()
            .WithParentActivityOrWindow(GetConsoleOrTerminalWindow)
            .WithBroker(new BrokerOptions(BrokerOptions.OperatingSystems.Windows) { Title = "PresencePlus" })
            .Build();
    }

    public async Task<GraphAccessToken> AcquireSilentAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (account is null)
        {
            var accounts = (await application.GetAccountsAsync()).ToArray();
            if (accounts.Length > 1)
                throw new MsalUiRequiredException("account_selection_required", "Choose the account PresencePlus should use.");
            account = accounts.SingleOrDefault();
        }
        var result = await application.AcquireTokenSilent(Scopes, account ?? PublicClientApplication.OperatingSystemAccount)
            .ExecuteAsync(cancellationToken);
        return Save(result);
    }

    public async Task<GraphAccessToken> AcquireInteractiveAsync(string? claims, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var request = application.AcquireTokenInteractive(Scopes).WithAccount(account);
        if (!string.IsNullOrEmpty(claims)) request = request.WithClaims(claims);
        return Save(await request.ExecuteAsync(cancellationToken));
    }

    private GraphAccessToken Save(AuthenticationResult result)
    {
        account = result.Account ?? throw new InvalidOperationException("Windows sign-in did not return an account.");
        return new(result.AccessToken, result.ExpiresOn, $"{result.TenantId}/{account.HomeAccountId.Identifier}");
    }

    private static IntPtr GetConsoleOrTerminalWindow()
    {
        var console = GetConsoleWindow();
        if (console == IntPtr.Zero)
            throw new InvalidOperationException("Microsoft sign-in needs a console window. Start PresencePlus from PowerShell, or configure Task Scheduler to run only when you are logged on without hiding its console.");
        // Windows Terminal hosts the visible root window rather than the console HWND.
        var root = GetAncestor(console, 3);
        return root == IntPtr.Zero ? console : root;
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);
}
