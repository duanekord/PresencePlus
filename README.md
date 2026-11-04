# DoorPulse

A lightweight .NET 10 service that translates Microsoft Teams presence and Outlook
calendar events into an office-door LED. Run a console simulation first, then
switch to an ESP32 or another Wi-Fi LED controller without changing the Graph integration.
No external NuGet packages are required.

## Run locally

Install the .NET 10 SDK, then from the repository root:

```sh
dotnet run --project src/DoorPulse --no-launch-profile -- --Demo=true --urls=http://127.0.0.1:5080
```

The console logs LED changes; `GET http://127.0.0.1:5080/` returns the current LED
JSON (no calendar titles or user identity). Demo mode starts available with a
meeting six minutes away, progresses through both reminders and a one-minute
meeting, then returns to available. Omit `--Demo=true` for an unknown/gray idle
simulation. Demo and Graph modes cannot run together.

```sh
dotnet build src/DoorPulse
dotnet run --project tests/DoorPulse.Checks
```

The dependency-free executable checks cover priorities, exact reminder boundaries,
overlapping meetings, local countdowns, deletions, UTC conversion, and webhook validation.

## Connect Microsoft 365

1. Register a **single-tenant public client** in Microsoft Entra ID. Enable
   **Allow public client flows** (device-code flow); no client secret is needed.
2. Add delegated Microsoft Graph permissions **User.Read**, **Presence.Read.All**,
   **Calendars.Read**, and **offline_access**. Obtain tenant administrator consent
   where required. Use a work/school account with Teams and an Exchange mailbox.
   Presence subscriptions do **not** support application-only authentication.
3. Expose **only** `/graph/notifications` through a trusted, publicly reachable
   HTTPS reverse proxy/tunnel to this service. Forward POST requests and the
   validation query string unchanged. Keep the status endpoint private. Terminate
   TLS at the proxy, limit request sizes/rates, and do not log webhook bodies,
   device sign-in codes, or tokens. The callback must be reachable before sign-in.
4. Configure the following environment variables and start the service:

```sh
export Graph__Enabled=true
export Graph__TenantId='<tenant GUID>'
export Graph__ClientId='<application GUID>'
export Graph__NotificationUrl='https://your-public-host/graph/notifications'
dotnet run --project src/DoorPulse --no-launch-profile -- --urls=http://127.0.0.1:5080
```

5. Follow the sign-in URL and one-time code printed on the local console.
   Access/refresh tokens stay **in memory only** and are refreshed automatically.
   Restarting requires sign-in again; tenant policy or revoked consent can also
   require another interactive sign-in. Restrict access to local console logs.
   Device-code authentication may be blocked by tenant policy.

DoorPulse creates basic (not encrypted rich) subscriptions to
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
its previous matching subscriptions and rebuilds state. The notification queue and
tokens are not durable; a restart restores current state, not historical transitions.
Graph errors retry after 30 seconds; device errors retry each second. During a Graph
outage the last successfully fetched state/calendar remains in use, so the indicator
can be stale. Monitor service logs; this is a family convenience indicator, not a
safety-critical availability guarantee.

Official Graph references:
[presence subscriptions](https://learn.microsoft.com/en-us/graph/changenotifications-for-presence),
[Outlook notifications](https://learn.microsoft.com/en-us/graph/outlook-change-notifications-overview),
[webhook validation](https://learn.microsoft.com/en-us/graph/change-notifications-delivery-webhooks).

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
2xx after applying the state. The device owns pulse animation; DoorPulse sends
updates only when state changes, retrying failed writes. Color channels may need
board-specific PWM/gamma correction. Firmware is deliberately a separate adapter
concern; no particular ESP32 board or LED wiring is assumed.

Use HTTPS when transmitting a key, or an isolated trusted LAN for HTTP-only boards.
Never expose the device endpoint to the public internet. Configure a device-side
watchdog/fallback if you need LEDs to turn off when DoorPulse disconnects.

## Components

- `StatusRules` / `OfficeState`: deterministic mapping and synchronized current inputs.
- `GraphTokenProvider`: delegated device sign-in and token refresh.
- `GraphWorker` / `GraphNotifications`: subscription lifecycle, authenticated triggers,
  and Graph state reconciliation.
- `LedWorker` / `ILedOutput`: local scheduling with console and Wi-Fi adapters.
