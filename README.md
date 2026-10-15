<p align="center">
  <img src="docs/airtray-logo.svg" alt="AirTray" width="720">
</p>

# AirTray

AirTray is a minimal Windows tray app for **AirPods battery information**.

It intentionally stays small and focused:

- **Tray indicator** for the current battery status
- **Left-click popup** for a compact battery overview
- **Right-click menu** for the essential actions only
- **Bluetooth diagnostics** for debugging advertisement parsing when needed

> AirTray only shows values that were actually decoded from Bluetooth advertisements. It does **not** invent battery values, case values, model names, or serial numbers.

---

## What AirTray does

AirTray listens for Apple Bluetooth Low Energy advertisements and extracts approximate AirPods battery levels.

The app is designed around exactly three UI surfaces:

1. **Tray icon** – quick status directly in the Windows taskbar
2. **Left-click popup** – modern compact battery view
3. **Right-click menu** – autostart, Bluetooth settings, diagnostics, and exit

No full main window, no unnecessary settings pages, and no artificial feature bloat.

---

## Features

- **Windows tray app** based on WinForms / .NET 8
- **Battery display** for left, right, and case when available
- **Freshness handling** for stale measurements
- **Autostart toggle** via the context menu
- **Bluetooth diagnostics capture** (opt-in, time-limited)
- **Crash logging** to `%LOCALAPPDATA%\AirTray\errors.log`
- **Windows installer** via Inno Setup
- **GitHub Actions workflow** for automated Windows builds

---

## Limitations

AirPods battery values from BLE packets are **approximate** and often arrive in **10% steps**.

Please note:

- battery values are not guaranteed to be precise percentages
- the **case** is only shown when a usable case value is present
- AirTray does **not guess** unsupported model names
- serial numbers are not shown unless they are actually available from the used signal source
- rotating BLE addresses are treated as transport identifiers, not user-facing “multiple devices”

---

## Repository structure

```text
.
├─ .github/
│  └─ workflows/
│     └─ windows-installer.yml
├─ docs/
│  └─ airtray-logo.svg
├─ installer/
│  └─ AirTray.iss
├─ AirTray.csproj
├─ Program.cs
├─ BleCapture.cs
├─ global.json
├─ NuGet.Config
└─ README.md
```

---

## Build the app locally

### Requirements

- Windows 10 or Windows 11
- .NET 8 SDK

### Publish

```powershell
dotnet restore AirTray.csproj -r win-x64
dotnet publish AirTray.csproj -c Release -r win-x64 --no-restore --self-contained false -p:PublishSingleFile=true -p:PublishReadyToRun=false -o publish
```

---

## Build the installer locally

### Additional requirements

- Inno Setup 6

The installer definition lives in `installer/AirTray.iss`.

It expects:

- the app publish output in `publish/`
- the official .NET 8 Windows Desktop Runtime installer in `runtime/`

You can compile it with the Inno Setup compiler (`ISCC.exe`).

---

## GitHub Actions

The workflow in `.github/workflows/windows-installer.yml` does the following on a Windows runner:

1. restore and publish the app
2. download the official **.NET 8 Windows Desktop Runtime (x64)**
3. verify the runtime package hash
4. build a proper Windows installer with **Inno Setup**
5. upload the installer artifact

Expected artifact name:

- `AirTray-Windows-Installer`

Expected installer filename:

- `AirTray-Setup-1.0.0-win-x64.exe`

---

## Diagnostics and privacy

AirTray can optionally record Apple BLE manufacturer payloads for debugging.

Important:

- diagnostics are **off by default**
- capture is limited in duration and file size
- device addresses are replaced with local aliases like `D1`, `D2`, ...
- raw Apple payloads can still contain device-related information

Always review diagnostic logs before sharing them publicly.

---

## Runtime and installation strategy

AirTray is intended to stay small.

Instead of distributing a huge self-contained EXE, the project uses this approach:

- application build: **framework-dependent**
- installer: **Inno Setup**
- runtime handling: bundled **.NET Desktop Runtime installer** when needed

This keeps the application payload slim while still allowing a user-friendly setup experience.

---

## Current status

AirTray is functional as a focused utility for AirPods battery display on Windows, but it is still a practical reverse-engineering project around Bluetooth advertisements.

That means BLE parsing may still need improvement for specific AirPods generations or firmware variations.

---

## License

No license file has been added yet.