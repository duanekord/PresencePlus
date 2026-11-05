# PresencePlus

A Windows tray app for **manually setting a Matter light's status color** over
your local network. Choose Available, Busy, Do not disturb, In a meeting,
Presenting, Away, or Off in the window or tray menu.

**Status is manual.** PresencePlus does not read your presence, calendar, or
activity from other applications. No account sign-in is required.

The app currently provides:

- Direct control of one Matter-over-Wi-Fi color light.
- Independently configurable status colors and adjustable brightness.
- A compact connection indicator, reconnect control, and automatic retries.
- Saved status, colors, brightness, and pairing across app restarts.
- Background operation in the Windows tray and optional startup at Windows login.

Bluetooth is used for first-time pairing. After pairing, control uses the local
Wi-Fi/IPv6 network.

## Download and run

Download the **PresencePlus-...-win-x64.zip** asset from the
[GitHub Releases](https://github.com/duanekord/PresencePlus/releases),
not GitHub's automatic **Source code** ZIP.
Early downloads may be marked **Pre-release** while the pull request is awaiting
approval; these are previews, not a claim that the changes are already on `main`.

1. Extract the entire ZIP into a permanent folder you can write to, such as
   `%LOCALAPPDATA%\PresencePlus\app`.
2. Run **PresencePlus.exe** from that folder. Keep the `Matter` folder beside it;
   the complete app is not a standalone executable.
3. Follow the pairing walkthrough below, then choose a status.

The portable download includes **.NET**, which runs the Windows interface, and
**Node.js**, which runs the Matter controller. Neither needs to be installed
separately or globally. You do not need an SDK, PowerShell, or an account to run
the app. No administrator installation or Windows service is required.

**Requirements:** Windows x64 (Windows 11 recommended), a supported Matter-over-Wi-Fi
color light, Bluetooth for first-time setup, and a local network with IPv6 and
multicast discovery. Have the light's Matter setup code and compatible Wi-Fi
credentials ready. Many lights, including the tested Cync strip, require 2.4 GHz
Wi-Fi. Internet access is needed during pairing for certificate verification.

This is an early release: tested hardware is a **GE Cync Indoor Light Strip 32ft**
(`CSTR32CBDM/MSS-1P`). Other hue/saturation-capable Matter-over-Wi-Fi lights may work
but are not yet verified. Thread commissioning, multiple lights, and automatic
status detection are not supported. Linux, macOS, and ARM64 packages are not provided.

Release archives have a companion `.sha256` file. To compare a download:

```powershell
Get-FileHash .\PresencePlus-0.1.0-win-x64.zip -Algorithm SHA256
```

Use the actual versioned filename you downloaded and compare the result with the
hash in its matching `.sha256` asset. A checksum checks
integrity, not publisher identity. Builds are currently **unsigned**; Windows may
show publisher/reputation warnings. Review the source, release origin, and your
organization's software policy before deciding whether to run the app.

## Connect your light: quick walkthrough

1. **Prepare the light.** Power it on, enable the PC's Bluetooth, and follow the
   manufacturer's instructions to enter **Matter commissioning mode**. Ordinary
   Bluetooth visibility is not enough. If the light is already commissioned,
   follow the manufacturer's instructions to open another pairing window or reset
   it. A factory reset removes existing pairings and routines; PresencePlus never
   resets the light automatically.
2. **Choose Pair light...** Enter the printed **Matter setup code**, the light's
   **Wi-Fi network name (SSID)**, and the **Wi-Fi password**. Confirm that the light
   is in commissioning mode and select **Pair light**. Keep it powered and nearby;
   setup can take up to three minutes. Enter credentials only in this local
   dialog, never in GitHub issues, chat, shell arguments, or logs.

![Pairing dialog showing empty Matter code, Wi-Fi name, and masked password fields](docs/screenshots/pairing.png)

3. **Wait for Connected.** Bluetooth securely commissions the light and provisions
   its Wi-Fi connection. Subsequent control uses the local network, not Bluetooth
   or a cloud-control service. The app saves its controller identity, so pairing
   is not repeated on Windows login or app restart. Keep the PC and light on the
   same reachable LAN; a guest network with client isolation will prevent control.
4. **Choose a status.** Use a window button or the notification-area menu. The
   small green connection indicator means the light acknowledged the selection.
   Hover over it for details if it says **Retrying**, or choose **Reconnect**.
   **Off** sends a real power-off command.

![Main window with manual statuses, compact connection indicator, brightness, and tray preferences](docs/screenshots/main.png)

5. **Make it yours.** Use **Colors...** to choose each status's color and **Save**
   to apply it. Adjust brightness as needed. Optionally select **Start PresencePlus
   when I sign into Windows**. Closing the window keeps the app in the tray;
   **Exit** stops it.

![Status color editor with independent swatches, reset, cancel, and save controls](docs/screenshots/colors.png)

Screenshots are rendered from the actual Windows forms with simulated output and
empty credential fields; they contain no real Wi-Fi details or pairing secrets.
This version supports **one paired light endpoint**. Pairing again does not
silently replace it. Keep the saved controller identity private; do not copy it
between PCs or run multiple controllers against copies of that identity.

## Technologies and architecture

| Component | Technology and role |
|---|---|
| Desktop app | C# / .NET 10, Windows Forms, native tray menu and color picker |
| Local controller | TypeScript, Matter.js 0.18.0, bundled Node.js 24.22.2 |
| Initial setup | Native Windows Bluetooth LE through `@matter/nodejs-ble`; production Matter device attestation |
| Normal control | Matter over the local Wi-Fi/IPv6 network, with multicast discovery |
| App/helper communication | Private JSON-lines standard-input/output pipes; no app HTTP listener |
| Preferences and pairing | Per-user JSON preferences and a separately permission-restricted Matter fabric store |
| Distribution | Self-contained Windows x64 ZIP, SHA256 checksum, GitHub Actions and GitHub Releases |

The Windows app owns one Matter controller process and communicates with it
through private pipes. Initial pairing uses internet access for production device
attestation; normal light-control commands are sent over the local network.

## Troubleshooting

| Symptom | What to check |
|---|---|
| Not paired | Choose **Pair light...** and complete commissioning once. |
| No device found during pairing | Confirm **Matter** pairing mode, PC Bluetooth, proximity, setup code, and that the commissioning window has not expired. |
| Wi-Fi setup fails | Check SSID/password and the light's supported band, commonly 2.4 GHz. |
| Retrying / reconnect timeout | Check power, PC/light LAN reachability, IPv6, multicast, guest isolation, VPNs, and relevant network policy. Hover for details; **Reconnect** retries immediately. |
| Attestation fails | Ensure internet access to CSA's production certificate service. Verification is intentionally not bypassed. |
| Runtime missing / app will not start | Extract the whole release ZIP, not just the executable or the source ZIP. Keep the `Matter` folder beside the executable. |
| Already running | Open the existing instance from the notification area, including hidden tray icons. |
| Does not run at Windows sign-in after moving/upgrading | Exit any old instance and launch the new copy once to refresh an existing startup registration. For older releases, uncheck and recheck **Start PresencePlus when I sign into Windows** in the new copy. Also check Windows Startup apps for a disabled entry. |
| Preferences cannot load | Review the startup error and `%LOCALAPPDATA%\PresencePlus\settings.json`; invalid values are surfaced instead of silently discarded. |
| Paired light still on after Exit | Expected: Exit stops control, not the light. Choose **Off** and wait for **Connected** first. |

## Status, colors, and brightness

| Status | Default color |
|---|---|
| Available | Green |
| Busy | Orange |
| Do not disturb | Red |
| In a meeting | Red |
| Presenting | Purple |
| Away | Yellow |
| Off | Actual Matter power-off command |

Choose **Colors...** to customize each lit status with the Windows color picker.
**Save** applies the palette; **Cancel** discards edits, and **Reset defaults**
restores the original colors when saved. Changing the active status's color
updates the light immediately. **Off** is always power-off, not an editable color.

Every status is a manual choice. All colors are solid.
The brightness setting scales the palette's RGB intensity; purple retains its
lower original intensity. Colors and brightness are applied before power-on to
avoid flashing the previous color. The tray icon reflects your selection; the
window uses a small connection dot and a short label. Green **Connected** means
the selection was acknowledged; amber indicates connecting, updating, or retrying.
Hover over the indicator for details, including actionable connection errors.
The same details are available to accessibility tools.

Without a pairing, status selection remains a preview and the indicator shows
**Not paired**; choose **Pair light...**. Changes are serialized, and the latest selection follows an
in-flight command. Failures are shown and retried after a short delay, without
blocking the UI. **Reconnect** retries immediately.

The app also reapplies the current selection every 30 seconds to detect and
recover from light restarts or lost state. A restarted helper triggers an
immediate reapply. This app therefore owns the light's selected color while it
is running; another controller's changes can be overwritten.

Minimizing or closing the window keeps the app in the tray. Double-click the
icon or choose **Open PresencePlus** to restore it. Choose **Exit** to stop both
the app and its helper. Only one app instance per Windows user can run.

Exiting, PC sleep, logout, and network loss do **not** automatically turn off the
hardware. Choose **Off** and wait for **Connected** before exiting if
needed. Use a device-side watchdog if automatic fail-safe behavior is required.

## Settings and security

Preferences are saved atomically to
`%LOCALAPPDATA%\PresencePlus\settings.json`. Your palette, last status, and brightness are
restored and sent on startup. Invalid settings produce a visible error rather
than silently resetting your choices.

Matter controller credentials are stored separately under
`%LOCALAPPDATA%\PresencePlus\Matter`, with current-user-only Windows permissions.
Protect this directory as a secret: deleting it loses the controller identity,
and copying it grants control of the paired light. Permissions are not encrypted
storage and do not protect against software already running as your user.
Keep this directory out of source control, shared logs, and unprotected backups.

Pairing verifies the device's attestation against CSA's production certificate
service. Certificate errors, warnings, test certificates, and unavailable trust
services block pairing. Raw protocol payloads are not logged.

The helper uses no listener or HTTP port for app communication. Do not expose
Matter services to the public internet. Your network must allow local IPv6 and
multicast discovery between the PC and light. The app scopes discovery to an
active Wi-Fi or Ethernet adapter with a gateway, preferring Wi-Fi, to avoid
Hyper-V/WSL virtual interfaces delaying reconnect. With no suitable adapter,
the Matter stack uses its default interface discovery.

## Start with Windows

Extract or publish to a permanent folder, run `PresencePlus.exe`, and select **Start
PresencePlus when I sign into Windows**. This creates only your own
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run\PresencePlus` entry, pointing
to that executable with `--minimized`. It requires no administrator privileges
or Windows service. Uncheck the option to remove the entry.

The independent **Start minimized to the tray** preference applies to manual
launches too; it does not enable Windows startup by itself. If startup was already
enabled, launching PresencePlus from a new folder refreshes the registration to
that copy before showing the window. You do not need to toggle the checkbox after
an upgrade. If startup was disabled, launching a new copy leaves it disabled.
Windows or organizational policies can still disable or restrict startup; the app
does not override those controls.

## Updating the app

To update a portable installation, choose **Exit**, extract the new archive into
the app folder or a new permanent folder, and run the new copy once. If startup
was enabled, this updates its registered executable path, even when the previous
folder has already been deleted. Windows cannot find a moved copy until you
launch it once. Preferences and pairing are kept outside the app
folder and survive upgrades. Do not delete `%LOCALAPPDATA%\PresencePlus\Matter`
or share it with another person. Extracting a release never imports somebody
else's pairing.

## Build and package from source

Requires Windows x64, PowerShell 7, and the .NET 10 SDK:

```powershell
# Complete runnable app, including .NET and the Matter helper:
.\scripts\Publish.ps1 -OutputDirectory .\artifacts\publish

# Redistributable ZIP and SHA256 file:
.\scripts\Package.ps1 -Version 0.1.0
```

`Publish.ps1` downloads the pinned Node.js distribution from nodejs.org and checks
its SHA256 against the official HTTPS checksum list before extraction. It restores
locked npm dependencies without install scripts, compiles the helper, and bundles
production dependencies. It does not install Node globally or replace Bluetooth
drivers. Supply `-NodeDirectory 'C:\path\to\node-v24.22.2-win-x64'` to reuse a trusted
full Node distribution, including npm.

The default publish is **self-contained** for `win-x64`. Its managed desktop
runtime is consolidated into `PresencePlus.exe` without trimming. Matter
JavaScript is bundled into a few modules with lazy platform imports; only the
Windows x64 Bluetooth native addon and its explicitly listed runtime assets are
shipped. Node.js stays in the external `Matter` folder. This avoids extracting
thousands of individual dependency files while retaining both runtimes.

Developers can opt into `-FrameworkDependent` for a smaller app that requires the
.NET 10 Desktop Runtime. `-Unbundled` on `Publish.ps1` or `Package.ps1` produces
the larger, original-style dependency layout for troubleshooting and comparison.
Use a fresh output folder when switching between layouts; publishing over an
old folder does not remove obsolete dependency files.

A plain `dotnet build` does not package the helper. Packaging always uses a fresh
staging folder, never your installed app or user-data folder; it includes runtime
and dependency license notices, but no personal settings or controller fabric.
Compact packages are limited to **100 files**. Packaging checks Windows Forms
startup, the actual native Bluetooth load path, fresh controller storage, IPC,
and helper shutdown; CI repeats these checks after extracting the final ZIP.

### Extraction measurements

On the development PC, three alternating extractions into fresh folders produced:

| Metric | Original ZIP | Compact ZIP |
|---|---:|---:|
| Files | 7,463 | 71 |
| Download size | 97.2 MiB | 81.1 MiB |
| Extraction range | 31.1-39.3 seconds | 1.4-3.3 seconds |
| Median extraction | 33.8 seconds | 1.5 seconds |

That is about **99% fewer files** and **96% lower median extraction time** in
this benchmark. Measurements used .NET's ZIP extraction API with security
settings unchanged, not Windows Explorer. OS/security caches were not reset;
the user's reported ten-minute Explorer extraction is not a comparable baseline.
Explorer and other PCs may give different results.

The compact Windows-runtime/form-load check took 2.4-2.9 seconds on first launch
and 0.4-0.6 seconds on repeat launch, with no files written to the designated
bundle-extraction directory. This check excludes light discovery/reconnect and is
not a full UI-readiness measurement. A separate same-source comparison against
an unbundled desktop publish measured 2.0 seconds versus 1.7 seconds initially,
then approximately 0.5 seconds for both layouts. No meaningful runtime-startup
regression was observed in those samples; this is not a controlled cold-cache test.

To reproduce the automated comparison (results are written under `artifacts`):

```powershell
.\scripts\Measure-Package.ps1 `
    -BaselineArchive .\artifacts\packages\PresencePlus-0.1.0-win-x64.zip `
    -Archive .\artifacts\packages\PresencePlus-0.2.0-rc.1-win-x64.zip
```

GitHub Actions runs checks and creates downloadable workflow artifacts on pull
requests and pushes to `main`. Publishing a version tag from reviewed `main`
also creates the public GitHub release and uploads the ZIP and checksum:

```powershell
git tag v0.1.0
git push origin v0.1.0
```

Use a new semantic version for each release. Do not retag an existing download.
Release publishing uses only the workflow's repository-scoped GitHub token.
Tags with a suffix, such as `v0.1.0-rc.1`, publish a clearly marked pre-release;
normal releases should be tagged from reviewed `main`. Branch protections still
apply: a successful build does not replace a required approving review.

The project has not yet selected an open-source license. Third-party components
retain their own licenses, included in the download's `licenses` directory,
`Matter\licenses`, `Matter\NODE-LICENSE.txt`, and `THIRD-PARTY-NOTICES.txt`.
`Matter\DEPENDENCIES.json` records bundled dependency versions and notice paths.

## Development checks

```powershell
dotnet run --project .\tests\PresencePlus.Checks
# Include real process/pipe lifecycle checks using a trusted Node executable:
dotnet run --project .\tests\PresencePlus.Checks -- --node 'C:\path\to\node.exe'

# Refresh documentation images from real forms with simulated output:
dotnet run --project .\tests\PresencePlus.Checks -- --screenshots .\docs\screenshots

# From src\PresencePlus.Matter, with the pinned Node distribution on PATH:
npm ci --ignore-scripts --no-fund --no-audit
npm run build
npm test
```

The dependency-free C# executable covers settings migration, status delivery,
color customization, brightness, retries, latest-selection wins, helper restarts, private storage,
tray UI, masking, and optional real IPC cancellation/protocol failures.
Helper checks cover RGB conversion, command ordering, actual power-off semantics,
input validation, credential-safe errors, and fail-closed attestation.

An opt-in hardware check changes the real light through every status and a custom color, then
restores your saved selection. Exit the tray app first:

```powershell
dotnet run --project .\tests\PresencePlus.Checks -- --hardware "$env:LOCALAPPDATA\PresencePlus\app\Matter"
```
