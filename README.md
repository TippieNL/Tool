# Pulse Monitor

A lightweight Windows system monitor: a modern dark dashboard showing CPU, GPU, memory, storage,
network and temperature data at a glance, with live history graphs, a tray presence and
configurable alerts.

Built for Windows 10 and 11 (x64) on .NET 10 and WPF, using
[LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) for sensor
access.

## What it shows

| Card | Metrics |
|---|---|
| **CPU** | Model, total and per-thread utilisation, clock, temperature, package power, core/thread count |
| **GPU** | One card per adapter (NVIDIA / AMD / Intel): model, utilisation, temperature, core clock, VRAM used/total, power |
| **Memory** | Used / total, usage percentage, available, module speed and layout |
| **Storage** | Per drive: letter, capacity, used/free, read and write rates, activity, SSD/HDD type, temperature |
| **Network** | Adapter, download and upload rates, session and adapter totals |
| **Temperatures** | Every readable sensor across CPU, GPU, motherboard and drives |

Plus a **Performance** page with 30–300 second history graphs, a **Sensors** page listing every
sensor and per-thread load, and a **System** page with host name, Windows build, uptime,
motherboard and BIOS version.

## Requirements

- Windows 10 (1809 or later) or Windows 11, x64
- Nothing else for the self-contained build; the framework-dependent build needs the
  [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)

## Building and running

```powershell
git clone https://github.com/TippieNL/Tool.git
cd Tool

dotnet build Tool.slnx -c Release
dotnet test  Tool.slnx -c Release

# Run the app
dotnet run --project src/SysMon.App
```

To produce distributable binaries:

```powershell
.\publish.ps1                          # self-contained single .exe (no runtime needed)
.\publish.ps1 -Profile FrameworkDependent   # small build, needs the .NET Desktop Runtime
.\publish.ps1 -Profile Both
```

Output lands in `.\publish\`.

## Administrator rights

The app **runs unelevated by default**, deliberately: requiring elevation would mean a UAC prompt
at every login for anyone using "start with Windows".

Without elevation these work normally:

- CPU utilisation (total and per-thread), clock
- Memory, storage capacity and throughput, network
- GPU utilisation, temperature, clock, VRAM and power (via the vendor driver)
- Drive temperatures and SMART-derived data

These generally require administrator rights, because LibreHardwareMonitor needs to load its kernel
driver to read them:

- CPU package temperature and power
- Motherboard and SuperIO sensors (board temperatures, fan speeds, voltages)

When they are unavailable the app shows `N/A` and offers a one-click **Restart as administrator**.
Nothing crashes and nothing is hidden — a missing sensor is reported as missing.

## Diagnosing a sensor that reads N/A

Run the probe. It exercises the same providers the UI uses and prints everything they found,
independently of the interface:

```powershell
dotnet run --project src/SysMon.Probe                 # summary of every metric
dotnet run --project src/SysMon.Probe -- --raw        # plus every raw sensor the library exposes
dotnet run --project src/SysMon.Probe -- --ticks 10 --interval 500
```

The output ends with the per-tick cost and the working set, which is the number to check if you
suspect the monitor itself is expensive.

If a metric is `N/A` in the probe too, the hardware or driver is not exposing it. If it appears in
`--raw` but not in the summary, that is a mapping bug worth reporting.

## Where things live

```
%AppData%\PulseMonitor\settings.json    settings (atomic writes, corrupt files auto-recovered)
%AppData%\PulseMonitor\logs\app.log     rolling log, 1 MB x 3
```

Settings → **Open data folder** opens that directory.

## Design notes

**Performance.** One background loop drives everything; there is no timer per card and the UI never
polls hardware. Providers are split across tiers — CPU, memory, GPU and network every tick; storage
and motherboard every five seconds; names, capacities and OS facts once at startup. WMI is used
only for that startup pass and never on the tick path; per-tick CPU utilisation comes from a single
`NtQuerySystemInformation` call rather than performance counters. Each tick results in exactly one
dispatcher marshal, which updates long-lived view models in place, and updates are coalesced so a
busy UI thread cannot accumulate a backlog. While minimised to the tray the UI work is skipped
entirely and the poll rate drops. Graphs are drawn by a custom element into one frozen geometry —
no charting library — over allocation-free ring buffers.

**Robustness.** Every provider is wrapped so that no hardware fault reaches the monitoring loop:
exceptions are caught, logged once per distinct message, and a provider that fails repeatedly is
parked and retried every 60 seconds rather than being allowed to fail on every tick. Every metric
is nullable end to end, so "sensor absent", "sensor failed" and "hardware removed" all have one
representation that renders as `N/A`. Disabling a sensor group in Settings closes it in the sensor
library rather than merely hiding it.

**Architecture.** Hardware code has no knowledge of the UI, and the scheduling policy lives in the
platform-independent core so it can be tested anywhere.

```
src/SysMon.Core/         models, monitoring loop, history buffers, settings, alerts, logging  (net10.0)
src/SysMon.Monitoring/   sensor library session, providers, Win32 interop            (net10.0-windows)
src/SysMon.App/          WPF UI, tray, notifications, Windows integration            (net10.0-windows)
src/SysMon.Probe/        console sensor diagnostic                                   (net10.0-windows)
tests/SysMon.Core.Tests/ unit tests for everything in Core                                   (net10.0)
```

Adding a sensor means writing one `IMetricProvider`, choosing its tier, and adding it to the list in
`MonitoringService.BuildProviders`. Nothing in the UI needs to change to make it fault-tolerant.

## Tests

```powershell
dotnet test Tool.slnx -c Release
```

Two suites:

- **`SysMon.Core.Tests`** (93 tests, any OS) — the history ring buffer (wrap, resize, gaps), tier
  scheduling and the live timer loop, alert hysteresis and re-notify throttling, settings
  round-trip and corrupt-file recovery, display formatting, and provider fault isolation.
- **`SysMon.App.Tests`** (Windows only) — renders every view and the main window against a real
  view model and fails on any WPF binding error. A broken binding throws nothing and draws
  nothing, so without these a mis-typed path shows up only as a blank value on screen. Also
  asserts that each page actually receives the view model as its DataContext.

Both run in CI on `windows-latest`, which is the only environment that validates this project
fully: package pruning (NU1510) and Windows framework references behave differently when
cross-targeting, so a Linux build can pass while a Windows build fails.

Hardware-specific behaviour cannot be unit tested; that is what `SysMon.Probe` is for.

## Licence

LibreHardwareMonitorLib is licensed under the MPL 2.0.
