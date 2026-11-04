# PresencePlus

A lightweight .NET 10 service that translates Microsoft Teams presence and Outlook
calendar events into an office-door LED. Run a console simulation first, then
switch to an ESP32 or another Wi-Fi LED controller without changing the Graph integration.
Microsoft 365 sign-in uses MSAL.NET with the Windows account broker (WAM).
Graph mode requires Windows 10 version 1703 or later and a logged-in interactive
user session; the local simulation also runs on other .NET-supported platforms.

## Run locally

Install the .NET 10 SDK, then from the repository root:

```sh
dotnet run --project src/PresencePlus --no-launch-profile -- --Demo=true --urls=http://127.0.0.1:5080
```

The console logs LED changes; `GET http://127.0.0.1:5080/` returns the current LED
JSON (no calendar titles or user identity). Demo mode starts available with a
meeting six minutes away, progresses through both reminders and a one-minute
meeting, then returns to available. Omit `--Demo=true` for an unknown/gray idle
simulation. Demo and Graph modes cannot run together.

```sh
dotnet build src/PresencePlus
dotnet run --project tests/PresencePlus.Checks
```

The executable checks (no test framework required) cover priorities, exact reminder
boundaries, overlapping meetings, local countdowns, deletions, UTC conversion,
webhook validation, and authentication recovery without opening a sign-in prompt.

## Connect Microsoft 365

1. Register a **single-tenant public client** in Microsoft Entra ID, or reuse your
   existing registration. Under **Authentication > Add a platform > Mobile and
   desktop applications**, add this custom redirect URI, replacing `<client-id>`
   with the application's client ID:

   ```text
   ms-appx-web://microsoft.aad.brokerplugin/<client-id>
   ```

   Keep **Allow public client flows** enabled. No client secret, certificate,
   web platform, or Azure hosting resource is needed. The broker redirect is
   separate from the public Graph notification URL.
2. Add delegated Microsoft Graph permissions **User.Read**, **Presence.Read.All**,
   **Calendars.Read**, and **offline_access**. Obtain tenant administrator consent
   where required. Use a work/school account with Teams and an Exchange mailbox.
   Presence subscriptions do **not** support application-only authentication.
3. Expose **only** `/graph/notifications` through a trusted, publicly reachable
   HTTPS reverse proxy/tunnel to this service. Forward POST requests and the
   validation query string unchanged. Keep the status endpoint private. Terminate
   TLS at the proxy, limit request sizes/rates, and do not log webhook bodies,
   tokens or authentication payloads. The callback must be reachable before Graph
   creates its subscriptions. A temporary tunnel hostname changes on restart;
   use a stable hostname for automatic startup.
4. Open PowerShell in the repository root, configure the following environment
   variables, and start the service. Keep your reverse proxy and tunnel running:

```powershell
$env:Graph__Enabled = 'true'
$env:Graph__TenantId = '<tenant GUID>'
$env:Graph__ClientId = '<application GUID>'
$env:Graph__NotificationUrl = 'https://your-public-host/graph/notifications'
dotnet run --project .\src\PresencePlus --no-launch-profile -- --urls=http://127.0.0.1:5080
```

5. PresencePlus first tries silent sign-in using an account known to WAM, or your
   Windows work account if no cached account exists. If interaction is required,
   complete the Windows Microsoft sign-in prompt and any MFA/consent steps.
   If several cached accounts exist, choose the intended work account rather than
   having PresencePlus silently pick one. There is no device-code flow.
   Windows manages broker authentication state; PresencePlus does not write
   refresh tokens to its own files. Restarts try silent sign-in again rather than
   always requiring interaction. Tenant policy, revoked consent, or account
   changes may still require sign-in, and WAM does not bypass Conditional Access.

Authentication failures log only the MSAL error code and correlation ID, not
tokens, claims, account identifiers, or raw authentication responses. Use these
diagnostics with your tenant administrator if access is blocked. After a failed
or canceled interactive attempt, another prompt waits at least five minutes;
silent attempts can still recover meanwhile. Keep the console accessible for
sign-in and diagnostics. Do not run Graph mode as a Windows service, as another
user via `runas`, or in a non-interactive scheduled task.

### Start when you log into Windows

First complete a manual sign-in and confirm Graph notifications work. Then:

1. Publish to a permanent folder outside the development checkout:

   ```powershell
   dotnet publish .\src\PresencePlus -c Release --self-contained false -o "$env:LOCALAPPDATA\PresencePlus\app"
   ```

   This framework-dependent publish requires the .NET 10 ASP.NET Core Runtime
   (or SDK) on the PC. Keep all published files, including broker native libraries.
2. In **Task Scheduler > Create Task**, use your Windows account and select
   **Run only when user is logged on**. Do not select **Hidden**, run as SYSTEM,
   or request elevated privileges. Add an **At log on** trigger for your user.
3. Set the action's **Program/script** to the full expanded path to the published
   `PresencePlus.exe`. Set **Start in** to its containing `app` folder.
   In **Add arguments**, provide your non-secret configuration on one line:

   ```text
   --Graph:Enabled=true --Graph:TenantId=<tenant GUID> --Graph:ClientId=<application GUID> --Graph:NotificationUrl=https://your-public-host/graph/notifications --urls=http://127.0.0.1:5080
   ```

   Replace the placeholders; do not include angle brackets or leave spaces inside
   the GUIDs. Environment variables set only in a PowerShell window do not carry
   over to a scheduled task. Supply any LED settings separately, and never put
   an LED API key in task arguments.
4. Configure restart-on-failure and **Do not start a new instance** if already
   running. Remove the default time limit that stops a long-running task.
   Configure power conditions as needed if using a laptop.
5. Configure your reverse proxy and stable-hostname tunnel to start at login too.
   Start them before PresencePlus (a task-trigger delay can help). Graph retries
   while connectivity is unavailable. Leave the console available so broker
   prompts have a parent window and you can see any required sign-in.

This is a user-session background process, not a fully hidden service. It can
usually sign in silently after setup, but may occasionally need you to interact.
PC sleep, logout, and network loss interrupt delivery; state is reconciled on
reconnection. No Azure resource group or hosted app is required for this setup.

PresencePlus creates basic (not encrypted rich) subscriptions to
`communications/presences/{signed-in-user-id}` and `users/{signed-in-user-id}/events`.
It verifies subscription IDs and constant-time checks random `clientState` values
before accepting notifications. It never follows notification-supplied resource URLs.
Validation tokens are echoed as plain text; accepted changes are coalesced into an
in-memory refresh signal so the webhook responds without waiting for Graph.

Subscriptions expire after 45 minutes and are renewed with ten minutes remaining.
Initial connection, renewals, notifications, and a six-hour safety reconciliation
fetch current presence and a two-day, paginated UTC calendar view (including recurring
instances). A one-second **local** timer handles reminders and meeting start/end;
there is no continuous Graph presence/calendar polling. Calendar cancellations,
deletions, declined events, and free/tentative events do not block availability.
Busy and out-of-office calendar entries are treated as meetings.

Run **one instance per app registration and callback URL**. On restart it removes
its previous matching subscriptions and rebuilds state. The notification queue
and local state are not durable; a restart restores current state, not historical
transitions. Authentication state is managed separately by the Windows broker.
Graph errors retry after 30 seconds; device errors retry each second. During a Graph
outage the last successfully fetched state/calendar remains in use, so the indicator
can be stale. Monitor service logs; this is a family convenience indicator, not a
safety-critical availability guarantee.

Official Graph references:
[presence subscriptions](https://learn.microsoft.com/en-us/graph/changenotifications-for-presence),
[Outlook notifications](https://learn.microsoft.com/en-us/graph/outlook-change-notifications-overview),
[webhook validation](https://learn.microsoft.com/en-us/graph/change-notifications-delivery-webhooks),
[Windows broker authentication](https://learn.microsoft.com/en-us/entra/msal/dotnet/acquiring-tokens/desktop-mobile/wam).

## Status priority

| Priority | Status | Color / effect |
|---|---|---|
| 1 | Presenting | Purple, solid |
| 2 | Do not disturb | Red, solid |
| 3 | Teams meeting/call or active busy calendar entry | Red, solid |
| 4 | Next meeting in ≤1 minute | Orange, fast pulse |
| 5 | Next meeting in ≤5 minutes | Yellow, pulse |
| 6 | Busy / available / away / offline / unknown | Orange / green / yellow / off / gray |

Reminders do not override presenting, DND, or an active meeting. Graph's `Presenting`
activity drives presentation detection; calendar entries cannot infer screen sharing.
Some Teams clients may report DND instead, which remains red.

## Wi-Fi hardware

Set `Led__Url` to the controller's full endpoint (for example
`http://192.168.1.50/led`) to replace console output with HTTP output.
Optionally set `Led__ApiKey`; it is sent in `X-Api-Key`. Store any real key in an
environment variable or secret manager, never in committed configuration.

Implement this idempotent contract on the ESP32:

```http
PUT /led
Content-Type: application/json

{"status":"MeetingInOneMinute","color":"#FF8000","effect":"pulse-fast"}
```

Accept `solid`, `pulse`, and `pulse-fast`, drive the RGB LED accordingly, and return
2xx after applying the state. The device owns pulse animation; PresencePlus sends
updates only when state changes, retrying failed writes. Color channels may need
board-specific PWM/gamma correction. Firmware is deliberately a separate adapter
concern; no particular ESP32 board or LED wiring is assumed.

Use HTTPS when transmitting a key, or an isolated trusted LAN for HTTP-only boards.
Never expose the device endpoint to the public internet. Configure a device-side
watchdog/fallback if you need LEDs to turn off when PresencePlus disconnects.

## Components

- `StatusRules` / `OfficeState`: deterministic mapping and synchronized current inputs.
- `WindowsBrokerAuthentication` / `GraphTokenProvider`: delegated Windows broker
  sign-in, silent token acquisition, and account-change recovery.
- `GraphWorker` / `GraphNotifications`: subscription lifecycle, authenticated triggers,
  and Graph state reconciliation.
- `LedWorker` / `ILedOutput`: local scheduling with console and Wi-Fi adapters.
