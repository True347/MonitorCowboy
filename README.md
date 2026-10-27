# MonitorCowboy 🤠🖥️

A [Flow Launcher](https://www.flowlauncher.com/) plugin to **switch your monitor's input source
and set its brightness and speaker volume over DDC/CI** — a rofi-style multi-layer menu under a
single keyword, so you never reach for the OSD buttons again.

```
mc            →  every external monitor, with its current input, brightness and volume
mc dell       →  filter monitors by name; a unique match drills straight in
mc 1          →  actions for monitor 1:  Input source / Brightness / Volume
mc 1 in       →  the inputs this monitor reports (✓ marks the active one) — Enter switches
mc 1 in hdmi  →  same, filtered to inputs whose name contains "hdmi"
mc 1 bri      →  the current brightness, with two step items: Brightness +5 / Brightness -5
mc 1 bri 70   →  set the brightness to exactly 70 (0 … the monitor's own maximum)
mc 1 vol      →  the current volume, with two step items: Volume +5 / Volume -5
mc 1 vol 30   →  set the volume to exactly 30 (0 … the monitor's own maximum)
```

Every write is **verified by reading the value back**, so the list never claims a change the
monitor did not make. Token synonyms: `in` = `input`, `bri` = `bright` = `brightness` = `b`,
and `vol` = `volume` = `v`.

---

## How it works

DDC/CI is slow: on real hardware a single VCP read blocks for around 75 ms, a write plus its
verification for a quarter of a second, and the capabilities handshake for **three and a half
seconds**. So the query path **never talks to a monitor**. Everything you see is rendered from a
warm in-memory cache; reads and writes are queued onto a background worker per monitor, and when
a result lands the plugin re-runs the current query so the list updates under the cursor.

The success value of `SetVCPFeature` means nothing on its own, so every write is followed by
reading the value back (up to three attempts). Verified → the cache updates and the entry shows
`✓`. Read-back failed outright → *"Sent — could not verify"*, which is the normal outcome when
the write just switched the display away from this PC. Read-back **succeeded but the value did
not move** → the monitor swallowed the write, and the entry says *"Rejected"* — for brightness
that also raises a toast, because it is how monitors behave when HDR, dynamic contrast, or a
picture mode is holding brightness locked. The set call itself failing → an error toast. Nothing
is ever reported as applied on the strength of a return code, and nothing fails silently — with
one exception you opt into per monitor: a monitor whose brightness changes but whose reported
value never does can be marked **write-only**, and its brightness writes then skip the read-back
and show as *"last set"* rather than as verified (see [Troubleshooting](#troubleshooting)).

All DDC/CI traffic passes through one process-wide lock. Windows serializes nothing, and
concurrency is unsafe even across *different* monitors — displays on one adapter, and monitors
daisy-chained over DisplayPort MST, share the same I2C plumbing. The per-monitor queues
therefore only order and merge work: repeated writes to the same feature collapse latest-wins,
so holding down `Volume +5` cannot flood the bus with a set-and-verify storm.

Capabilities are cached persistently, keyed by monitor device path, so the slow handshake happens
once per monitor rather than once per launch. Monitors whose capabilities string cannot be read —
a common real-world failure, since it is the most fragile DDC/CI command — are probed for the two
features directly and fall back to a generic input list rather than being written off.

---

## Install

**From the Flow Launcher plugin store** (once listed): open Flow, type
`pm install MonitorCowboy`, or find it in Settings → Plugin Store.

**From a release zip** (or to test before the store listing) — paste into Flow and press Enter:

```
pm install https://github.com/True347/MonitorCowboy/releases/latest/download/Flow.Launcher.Plugin.MonitorCowboy.zip
```

That URL always resolves to the latest [release](https://github.com/True347/MonitorCowboy/releases).
One zip serves both x64 and ARM64 hosts, and there is nothing to install alongside it — no
runtime, no helper executable. Restart Flow (or reload plugins) and type `mc`.

**From source** (developers): see [Development](#development).

---

## Usage

| You type | What happens |
|---|---|
| `mc` | Every external monitor, numbered. The subtitle carries the cached input, brightness and volume — plus `stale` when the last read failed, and `updating…` while a refresh is in flight. Features the monitor lacks are simply omitted, and one that never answered DDC/CI says so instead. |
| `mc dell` | Filters the list by name substring. A unique match drills straight into that monitor; an ambiguous one just narrows the list. |
| `mc 1` | That monitor's actions: **Input source**, **Brightness** and **Volume**, each with its current value. Only features its capabilities actually report are offered. |
| `mc 1 in` | The inputs this monitor accepts, read from its own capabilities string and named from the MCCS table; the subtitle shows the raw VCP value that will be written. `✓` marks the active input. Enter switches and closes the window — the display is probably going away anyway, and failures still surface as a toast. |
| `mc 1 in hdmi` | Same list, filtered to inputs whose name contains "hdmi". |
| `mc 1 bri` | The current brightness as `Brightness: 60 / 100`, plus `Brightness +5` and `Brightness -5` once the first read has landed. Both keep the window open, so Enter can be pressed repeatedly; steps compound from the newest requested value instead of the last value read back. |
| `mc 1 bri 70` | Adds a `Set brightness to 70` item, with the same range and number validation as volume. |
| `mc 1 vol` | The current volume as `Volume: 45 / 100`, plus `Volume +5` and `Volume -5`. Same behavior as brightness. |
| `mc 1 vol 30` | Adds a `Set volume to 30` item. A value above the monitor's own maximum is refused with the range it does accept, and a non-numeric token says so rather than reaching the hardware. |

Navigation is keyboard-first: Enter or Tab drills into the next layer, and every layer carries a
`← Back` item. **Shift+Enter** on a monitor opens maintenance actions — *Refresh values*,
*Re-read capabilities* (slow; use it after enabling DDC/CI in the OSD), and, where brightness is
supported, *Treat brightness as write-only* (see Troubleshooting). The numbers are list
positions: they hold until the set of attached monitors changes, and selection
history never reorders them.

---

## Requirements and limits

- Windows, with Flow Launcher **v2.1 or newer** (the plugin binds to the host's bundled SDK
  rather than shipping its own — see [Backend](#backend)).
- External monitors with **DDC/CI enabled**. Plenty of monitors ship with it switched off in the
  OSD menu, usually under a name like *DDC/CI*, *Auto Source* or *Monitor Control*.
- Laptop internal panels do not speak DDC/CI at all and are filtered out of the list.
- The input list is whatever the monitor advertises. USB-C has no standardized DDC/CI value, so
  a few monitors label it oddly; entries shown as `Input 0xNN` are exactly what the monitor
  reported and MonitorCowboy declines to guess a nicer name for them.
- Docks, KVMs and some hubs strip DDC/CI entirely; monitors that are off or asleep reject it
  until they wake.
- **HDR, dynamic contrast (DCR), and some picture modes lock brightness** on many monitors while
  they are active — the monitor still lists brightness as supported and still accepts the
  command, it just keeps its value. MonitorCowboy detects this by reading the value back and
  reports it as *Rejected* instead of pretending the change applied.
- Changes made with the monitor's own buttons produce no notification of any kind, so they show
  up on the next look, once the value cache expires (about 10 seconds).

---

## Cache & data

Nothing is ever written to the plugin folder. What persists is each monitor's parsed
capabilities, plus two settings — whether the diagnostic trace is on, and which monitors you
marked write-only for brightness — all stored through Flow's plugin JSON storage, which lives
under the Settings folder and therefore survives plugin updates:

```
%APPDATA%\FlowLauncher\Settings\Plugins\MonitorCowboy\PluginSettings.json
```

```jsonc
{
  "DiagnosticsEnabled": false,
  "CapabilitiesByDevicePath": {
    "\\\\?\\DISPLAY#DELA1CF#5&1234abcd&0&UID4353#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}":
      "(prot(monitor)type(lcd)model(U2723QE)cmds(01 02 03 07 0C E3 F3)vcp(02 10 60(0F 11 12 1B) 62 ...)mccs_ver(2.1))"
  },
  "BrightnessWriteOnlyDevicePaths": []
}
```

Three cache layers back the UI, and only the middle one touches disk:

| Layer | Holds | Lifetime |
|---|---|---|
| Topology | the monitor list, device paths, friendly names | rebuilt on every display topology change (debounced ~1 s) |
| Capabilities | the parsed `vcp(...)` table: which features exist, which input values are accepted | **persisted**; re-read on demand — or, while a monitor reports DDC/CI as unsupported, retried whenever you query it, at most once a minute, since re-enabling it in the OSD raises no Windows event |
| Values | current input, brightness and volume, and their maximums | in memory, ~10 s TTL; updated in place by a verified write — or, on a write-only monitor, by the brightness write itself, which later reads then do not overwrite |

Deleting that file costs one slow re-read per monitor, and any write-only marks. Flow's
**Reload Plugin Data** clears the capabilities cache — but not the write-only marks, which are
your choices rather than cached data — and re-enumerates everything from scratch.

Diagnostic traces, when you turn them on, are written alongside it in a `diagnostics\` subfolder —
the newest three, capped at 5 MB each. They record monitor device paths and the text typed after
the keyword, so read one before sending it anywhere.

---

## Troubleshooting

- **"No DDC/CI-capable monitors found"** — every attached display is either an internal panel or
  is not answering. Check DDC/CI in the OSD, wake the monitor, and try a direct cable: docks and
  KVMs are the usual culprits.
- **"This monitor does not support DDC/CI control"** — capabilities could not be read *and*
  neither feature answered a direct probe. Enable DDC/CI in the OSD, then run `mc` again — the
  monitor is re-probed on a query, at most once a minute, or immediately via Shift+Enter →
  *Re-read capabilities*.
- **"Generic input list"** — the monitor answers VCP but would not report its own inputs, so the
  list is the standard MCCS table. Entries it lacks are harmless; the write simply comes back
  unverified.
- **"Sent — could not verify"** — expected right after switching an input away from this PC: the
  monitor stops answering on that port, so there is nothing to read back. Nothing went wrong.
- **"Rejected — the monitor kept its previous value"** — the monitor answered the read-back and
  reported the value unchanged: it swallowed the write. For brightness this almost always means
  HDR, dynamic contrast, or a picture mode is holding it locked — turn that off (or switch
  modes) and try again. The brightness menu keeps a note about the rejection for a few minutes
  so a locked monitor does not look healthy again after the next background read.
- **Brightness does change, but every write says "Rejected"** — some monitors apply a DDC/CI
  brightness write yet keep reporting the value from their own OSD menu, which only the front
  panel moves (seen on an AOC Q27G3XMN). To the plugin that is indistinguishable from an HDR
  lock, so you have to tell it: Shift+Enter on the monitor (or in its brightness menu) →
  *Treat brightness as write-only*. Brightness writes to that monitor then skip the read-back:
  the rejected toast stops, the value shown is the one you last set — marked *"last set"*, or
  *"as reported"* before your first write — and the ±5 steps build on it. A set call that
  actually fails is still reported. The cost is the lock detection itself: on that monitor a
  write that HDR or a picture mode swallows now looks applied. Only brightness is affected, the
  mark is remembered per monitor device path, and *Verify brightness writes again* in the same
  menu undoes it.
- **A value looks out of date** — front-panel changes are invisible to DDC/CI until the next
  read. Wait out the ~10 s TTL or force it with Shift+Enter → *Refresh values*.
- **Nothing happens / errors** — check Flow's logs at `%APPDATA%\FlowLauncher\Logs\`; capability
  reads and probe results are logged per monitor.
- **Something is wrong and the log does not explain it** — type `mc debug on`. Every DDC/CI
  exchange, with its latency and outcome, is then recorded to a file under the folder below, which
  `mc debug` will open for you. It costs nothing while off, which is the default. `mc debug off`
  stops it; the file stays.

---

## Backend

**No external executable and no third-party DDC library.** MonitorCowboy P/Invokes the Windows
Monitor Configuration API in-process (`dxva2.dll`, plus display enumeration from `user32.dll`):

```
EnumDisplayMonitors → GetNumberOfPhysicalMonitorsFromHMONITOR → GetPhysicalMonitorsFromHMONITOR
→ GetCapabilitiesStringLength / CapabilitiesRequestAndCapabilitiesReply   (slow; once per monitor)
→ GetVCPFeatureAndVCPFeatureReply / SetVCPFeature                         (every read and write)
→ DestroyPhysicalMonitor                                                  (immediately after)
```

The high-level API (`GetMonitorBrightness` and friends) has no functions for input source or
volume, so low-level VCP is the only route that avoids bundling an executable. Because
`GetPhysicalMonitorsFromHMONITOR` mostly reports descriptions like "Generic PnP Monitor",
identity and naming come from `QueryDisplayConfig` + `DisplayConfigGetDeviceInfo` instead —
`monitorDevicePath` as the cache key, `monitorFriendlyDeviceName` for display, and
`outputTechnology` to drop internal panels — joined to each `HMONITOR` by GDI device name.

The only NuGet reference is the Flow Launcher SDK, and it is deliberately **not** shipped in the
release zip: the host loads plugins in-process and dedups assemblies by exact identity, so a
bundled copy would break type identity the moment the host upgrades its SDK. That is what sets
the minimum supported host at Flow Launcher with SDK 5.3.1 (v2.1.x and newer).

To publish to the Flow plugin store, see [publish/PUBLISHING.md](publish/PUBLISHING.md).

---

## Development

```
plugin.json                     # manifest (keyword: mc, Language: csharp)
MonitorCowboy.csproj            # net9.0-windows; Flow SDK referenced with ExcludeAssets=runtime
src/Main.cs                     # IAsyncPlugin / IContextMenu / IAsyncReloadable / IDisposable
src/FlowSettingsStore.cs        # capabilities cache + per-monitor choices over Flow's plugin JSON storage
src/Core/MonitorService.cs      # owns every monitor and worker; topology rebuilds
src/Core/DdcWorker.cs           # one serialized DDC queue per monitor; verify-after-set
src/Core/MonitorEntry.cs        # mutable per-monitor state → immutable snapshots
src/Core/CapabilitiesParser.cs  # tolerant vcp(...) parser (never throws)
src/Core/VcpDefs.cs             # VCP codes + input-source names
src/Interop/                    # dxva2 / user32 P/Invoke, identity chain, topology watcher
src/Ui/QueryRouter.cs           # query text → typed intent (pure)
src/Ui/ResultFactory.cs         # intent + snapshots → Flow results (never does I/O)
tests/MonitorCowboy.Tests/      # xUnit: parser, router, worker, service
Images/                         # icon set
tools/gen_icons.py              # regenerate icons (Pillow)
```

```powershell
dotnet build MonitorCowboy.csproj -c Release
dotnet test tests\MonitorCowboy.Tests\MonitorCowboy.Tests.csproj
```

Requires the .NET 9 SDK. The logic layer compiles into a plain `net9.0` test project and runs on
any OS — the native API sits behind an `INativeMonitorApi` seam, so the worker and service are
tested against a fake that can fail reads, reject writes, or accept a write without applying it.
Only the interop layer is Windows-only, and only real hardware can exercise it.

### Why no monitor handle is ever kept

Physical monitor handles acquired once at startup have been observed going permanently stale on
some driver stacks: every later exchange fails with I2C error `0xC0262582` while other DDC tools
keep working on the same machine — the difference being that they open and close a handle per
operation. So the interop layer acquires the handle fresh for every operation and destroys it
before returning. That also disposes of handle leaks and of any need to invalidate handles when
the display topology changes.

### Why one process-wide lock instead of one per monitor

Parallel DDC/CI turned out to be unsafe across monitors, not just within one: on real hardware,
running capability reads and VCP probes concurrently made them fail across the board, while the
same probes succeeded one monitor at a time. Displays on a shared adapter and DisplayPort MST
chains share I2C/aux plumbing, so exchanges corrupt each other. The per-monitor queues remain —
they are what orders operations and merges redundant writes — but the bus itself is entered by
exactly one operation at a time.

---

## Credits

- [Flow Launcher](https://github.com/Flow-Launcher/Flow.Launcher) — the launcher and plugin API.
- [Monitorian](https://github.com/emoacht/Monitorian) — reference for pairing `HMONITOR`s with
  display-config targets, and for capabilities parsing.
- [ddcutil](https://www.ddcutil.com/) — its documentation is the clearest public account of
  DDC/CI's real latencies and failure modes.
- Inspired by [Twinkle Tray](https://github.com/xanderfrangos/twinkle-tray) and rofi-style
  single-entry menus.

## License

[MIT](LICENSE)
