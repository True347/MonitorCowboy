# MonitorCowboy

Control your monitors over DDC/CI straight from [Flow Launcher](https://www.flowlauncher.com/) — switch the input source and adjust speaker volume without reaching for the OSD buttons.

## Usage

| Query | What you get |
|---|---|
| `mc` | Every DDC/CI-capable monitor with its current input and volume |
| `mc 1` | Actions for monitor 1: **Input source**, **Volume** |
| `mc 1 in` | The inputs this monitor actually supports (✓ marks the active one) — Enter switches |
| `mc 1 vol` | Current volume plus +5 / −5 step items |
| `mc 1 vol 30` | Set volume to 30 |
| `mc dell` | Filter monitors by name; a unique match drills straight in |

Navigation is keyboard-first: Enter or Tab drills in, the `← Back` item goes up
one level. Shift+Enter on a monitor offers maintenance actions (refresh values,
re-read capabilities).

## Why it feels instant

DDC/CI is slow (a single read costs ~40 ms and a capabilities handshake can
take seconds), so MonitorCowboy never talks to the monitor while you type.
Everything you see is served from a warm cache; reads and writes run on a
background worker per monitor, writes are verified by reading the value back,
and the view refreshes itself when the result lands. Capabilities are cached
persistently, so the slow handshake happens once per monitor — not once per
launch.

## Requirements and limits

- Windows with Flow Launcher v2.1 or newer.
- External monitors with DDC/CI enabled (check the monitor's OSD menu; it is
  sometimes shipped disabled). Laptop internal panels do not speak DDC/CI and
  are not listed.
- The input list comes from the monitor's own capabilities report. USB-C has
  no standardized DDC/CI value, so a few monitors label it oddly — entries
  shown as `Input 0xNN` are exactly what the monitor advertises.
- Docks, KVMs and some hubs strip DDC/CI; monitors that are off or asleep
  reject it temporarily.

## Install

From the Flow Launcher plugin store (`pm install MonitorCowboy`), or manually:
`pm install <release zip URL>` using the latest
[release](https://github.com/True347/MonitorCowboy/releases).

## Building from source

```
dotnet build MonitorCowboy.csproj -c Release
dotnet test tests/MonitorCowboy.Tests/MonitorCowboy.Tests.csproj
```

Requires the .NET 9 SDK. The logic-layer tests run on any OS; the plugin
itself is Windows-only.

## License

[MIT](LICENSE)
