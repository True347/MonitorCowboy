# Publishing MonitorCowboy

## How releases work

Every push to `main` that touches shippable files runs `publish-release.yml`:
it reads `Version` from `plugin.json`, and if no `v{Version}` release exists
yet it tests, publishes, packages `Flow.Launcher.Plugin.MonitorCowboy.zip`
(plugin dll + deps.json + plugin.json + Images + LICENSE + README; never the
Flow SDK dll) and creates the GitHub release. Pushes while the current version
is already released are no-ops, so committing freely is safe — cutting a
release is done by bumping `Version` in `plugin.json`.

## Pre-release checklist (real hardware)

- `pm install <release zip URL>` into a real Flow Launcher on Windows.
- For every attached monitor: capabilities parse matches the OSD (inputs
  listed = inputs that exist), input switching works and verifies, volume
  set/step works, values recover after monitor sleep/wake and cable replug.
- Front-panel changes show up after the cache TTL (~10 s).

## First-time store listing (one-off, manual)

1. Verify the release zip layout: `plugin.json` at the zip root.
2. Smoke-test `pm install <release zip URL>`.
3. Confirm the CDN icon resolves:
   `https://cdn.jsdelivr.net/gh/True347/MonitorCowboy@main/Images/app.png`
4. Fork `Flow-Launcher/Flow.Launcher.PluginsManifest`, add
   `plugins/MonitorCowboy-baf19c15c7054fcfb2ad422fb9dc161d.json` (copy from
   this folder) **on the `plugin_api_v2` branch**, and open a PR targeting
   `plugin_api_v2`. A PR against the wrong branch will not be listed.
5. Wait for manual approval by the Flow Launcher team. Manifest CDN
   propagation can take days.

## Every later release

1. Bump `Version` in `plugin.json`, push to `main`.
2. The store's auto-updater picks up the new GitHub release within ~3 hours
   and re-points `UrlDownload` itself. No further manifest PRs are needed.
