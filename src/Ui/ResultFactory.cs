using Flow.Launcher.Plugin;
using MonitorCowboy.Core;

namespace MonitorCowboy.Ui;

/// <summary>
/// Renders a <see cref="QueryIntent"/> into Flow results, exclusively from
/// snapshots — never from live DDC/CI state. Contracts enforced on every
/// result: AddSelectedCount=false (selection history must not reorder the
/// numeric grammar) and a non-null AutoCompleteText carrying the action
/// keyword (Flow falls back to the bare Title otherwise, which would eject
/// the user from the plugin on Tab).
/// </summary>
public sealed class ResultFactory
{
    private const string IconMonitor = "Images/monitor.png";
    private const string IconInput = "Images/input.png";
    private const string IconVolume = "Images/volume.png";
    private const string IconBrightness = "Images/brightness.png";
    private const string IconBack = "Images/back.png";
    private const string IconWarning = "Images/warning.png";
    private const string IconError = "Images/error.png";

    private const int VolumeStep = 5;
    private const int BrightnessStep = 5;

    private readonly IPublicAPI _api;
    private readonly MonitorService _service;
    private readonly string _pluginDirectory;
    private readonly Action _onWindowDismissed;
    private readonly IDiagnosticsControl _diagnostics;
    private readonly IDiag _trace;

    /// <param name="diagnostics">Read/control side, rendered by the debug view.</param>
    /// <param name="trace">Write side, used to record actions that fault.</param>
    public ResultFactory(
        IPublicAPI api,
        MonitorService service,
        string pluginDirectory,
        Action onWindowDismissed,
        IDiagnosticsControl diagnostics,
        IDiag trace)
    {
        _api = api;
        _service = service;
        _pluginDirectory = pluginDirectory;
        _onWindowDismissed = onWindowDismissed;
        _diagnostics = diagnostics;
        _trace = trace;
    }

    /// <summary>Query prefix for building navigation targets. Empty for global-keyword configurations.</summary>
    public static string PrefixFor(string actionKeyword)
        => string.IsNullOrEmpty(actionKeyword) || actionKeyword == "*" ? "" : actionKeyword + " ";

    public List<Result> Build(QueryIntent intent, IReadOnlyList<MonitorSnapshot> monitors, string actionKeyword)
    {
        var prefix = PrefixFor(actionKeyword);

        return intent switch
        {
            MonitorListIntent list => BuildMonitorList(monitors, list.Filter, prefix),
            MonitorMenuIntent menu => BuildMonitorMenu(menu.Monitor, prefix),
            InputMenuIntent input => BuildInputMenu(input.Monitor, input.Filter, prefix),
            VolumeMenuIntent volume => BuildVolumeMenu(volume.Monitor, volume.ValueToken, prefix),
            BrightnessMenuIntent brightness => BuildBrightnessMenu(brightness.Monitor, brightness.ValueToken, prefix),
            DebugMenuIntent debug => BuildDebugMenu(debug.Argument, prefix),
            _ => BuildMonitorList(monitors, "", prefix),
        };
    }

    public Result ErrorItem(string message, string view) => Item(
        title: "MonitorCowboy error",
        subtitle: message,
        icon: IconError,
        score: 0,
        autoComplete: view,
        action: _ => false);

    private List<Result> BuildMonitorList(IReadOnlyList<MonitorSnapshot> monitors, string filter, string prefix)
    {
        if (monitors.Count == 0)
        {
            return
            [
                Item(
                    "No DDC/CI-capable monitors found",
                    "Laptop internal panels cannot be controlled; external monitors may be off, asleep, or have DDC/CI disabled in their OSD.",
                    IconWarning, 0, prefix, _ => false),
            ];
        }

        var results = new List<Result>();
        foreach (var m in monitors)
        {
            if (filter.Length > 0 && !m.FriendlyName.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;

            var target = $"{prefix}{m.Index} ";
            results.Add(Item(
                $"{m.Index}  {m.FriendlyName}",
                StatusLine(m),
                IconMonitor,
                Math.Max(1, 100 - (m.Index - 1) * 10),
                target,
                _ => { _api.ChangeQuery(target, true); return false; },
                contextData: m.DevicePath));
        }

        return results;
    }

    private List<Result> BuildMonitorMenu(MonitorSnapshot m, string prefix)
    {
        var results = new List<Result>();
        var view = $"{prefix}{m.Index} ";

        switch (m.CapsState)
        {
            case CapsState.Pending:
                results.Add(Item(
                    "Reading capabilities…",
                    "First contact with this monitor can take a few seconds.",
                    IconMonitor, 50, view, _ => false, contextData: m.DevicePath));
                break;

            case CapsState.Unsupported:
                results.Add(Item(
                    "This monitor does not support DDC/CI control",
                    "Enable DDC/CI in the monitor's OSD menu if available, then re-read capabilities via the context menu (Shift+Enter).",
                    IconWarning, 50, view, _ => false, contextData: m.DevicePath));
                break;

            default:
                if (m.SupportsInput)
                {
                    var target = $"{prefix}{m.Index} in ";
                    var subtitle = m.CapsProbed
                        ? $"Current: {InputPart(m)} · generic list (monitor did not report its own)"
                        : $"Current: {InputPart(m)}";
                    results.Add(Item(
                        "Input source",
                        subtitle,
                        IconInput, 50, target,
                        _ => { _api.ChangeQuery(target, true); return false; },
                        contextData: m.DevicePath));
                }

                if (m.SupportsBrightness)
                {
                    var target = $"{prefix}{m.Index} bri ";
                    var subtitle = m.BrightnessRecentlyRejected
                        ? $"Current: {BrightnessPart(m)} · a recent write was rejected — may be locked by HDR / picture mode"
                        : $"Current: {BrightnessPart(m)}";
                    results.Add(Item(
                        "Brightness",
                        subtitle,
                        IconBrightness, 45, target,
                        _ => { _api.ChangeQuery(target, true); return false; },
                        contextData: m.DevicePath));
                }

                if (m.SupportsVolume)
                {
                    var target = $"{prefix}{m.Index} vol ";
                    results.Add(Item(
                        "Volume",
                        $"Current: {VolumePart(m)}",
                        IconVolume, 40, target,
                        _ => { _api.ChangeQuery(target, true); return false; },
                        contextData: m.DevicePath));
                }

                if (!m.SupportsInput && !m.SupportsBrightness && !m.SupportsVolume)
                {
                    results.Add(Item(
                        "No controllable features",
                        "This monitor's capabilities list none of input source (0x60), brightness (0x10), or speaker volume (0x62).",
                        IconWarning, 50, view, _ => false, contextData: m.DevicePath));
                }

                break;
        }

        results.Add(BackItem(prefix));
        return results;
    }

    private List<Result> BuildInputMenu(MonitorSnapshot m, string filter, string prefix)
    {
        if (m.CapsState != CapsState.Ready || !m.SupportsInput)
            return BuildMonitorMenu(m, prefix);

        var results = new List<Result>();
        var view = $"{prefix}{m.Index} in ";

        if (m.InputValues.Count == 0)
        {
            results.Add(Item(
                "Monitor reports no selectable inputs",
                "The capabilities string lists VCP 0x60 without any values.",
                IconWarning, 50, view, _ => false));
        }

        var score = 100;
        foreach (var value in m.InputValues)
        {
            var name = InputSourceNames.NameOf(value);
            if (filter.Length > 0 && !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;

            var isCurrent = m.CurrentInput.HasValue && InputSourceNames.SameInput(m.CurrentInput.Value, value);
            var subtitle = $"Switch {m.FriendlyName} to {name} (0x{value:X2})";
            if (m.PendingInput is { } pending && InputSourceNames.SameInput(pending.Target, value))
            {
                subtitle = pending.Phase switch
                {
                    OpPhase.Pending => "Applying…",
                    OpPhase.Unverified => "Sent — could not verify (expected when the input switched away).",
                    OpPhase.Failed => "Set failed — the monitor rejected the command.",
                    // Soft wording on purpose: a slow switch can keep reporting
                    // the old input for the whole verify window.
                    OpPhase.Rejected => "Sent, but the monitor reports the input did not change.",
                    _ => subtitle,
                };
            }

            var devicePath = m.DevicePath;
            results.Add(Item(
                (isCurrent ? "✓ " : "") + name,
                subtitle,
                IconInput,
                score,
                view,
                _ =>
                {
                    // Switching input dismisses the launcher: the display is
                    // likely moving away anyway, and failures still surface
                    // as a toast. Volume keeps the window (steps are
                    // press-repeatable); this is input-only by design.
                    if (!_service.RequestWrite(devicePath, Vcp.InputSource, value))
                        NotifyRebuilding();
                    _onWindowDismissed();
                    return true;
                }));
            score = Math.Max(1, score - 5);
        }

        if (m.CapsProbed)
        {
            results.Add(Item(
                "Generic input list",
                "This monitor answers DDC/CI but did not report its own inputs; entries it lacks simply won't verify.",
                IconWarning, 2, view, _ => false));
        }

        results.Add(BackItem($"{prefix}{m.Index} "));
        return results;
    }

    private List<Result> BuildVolumeMenu(MonitorSnapshot m, string valueToken, string prefix)
    {
        if (m.CapsState != CapsState.Ready || !m.SupportsVolume)
            return BuildMonitorMenu(m, prefix);

        var results = new List<Result>();
        var devicePath = m.DevicePath;
        var view = $"{prefix}{m.Index} vol ";

        results.Add(Item(
            m.CurrentVolume.HasValue && m.VolumeMax > 0
                ? $"Volume: {m.CurrentVolume} / {m.VolumeMax}"
                : "Volume: reading…",
            VolumeStatus(m),
            IconVolume, 100, view, _ => false));

        if (valueToken.Length > 0)
        {
            if (!uint.TryParse(valueToken, out var requested))
            {
                results.Add(Item(
                    $"Not a number: '{valueToken}'",
                    "Type a volume value, e.g. 30.",
                    IconError, 200, view, _ => false));
            }
            else if (m.VolumeMax == 0)
            {
                // Never send an unvalidated value to the hardware: until the
                // monitor's maximum has been read there is no range to check.
                results.Add(Item(
                    "Volume range not read yet",
                    "Try again in a moment — the monitor's maximum has not been read.",
                    IconWarning, 200, view, _ => false));
            }
            else if (requested > m.VolumeMax)
            {
                results.Add(Item(
                    $"Out of range (0–{m.VolumeMax})",
                    $"This monitor accepts volume values up to {m.VolumeMax}.",
                    IconError, 200, view, _ => false));
            }
            else
            {
                results.Add(Item(
                    $"Set volume to {requested}",
                    $"Apply to {m.FriendlyName}.",
                    IconVolume, 200, view,
                    _ =>
                    {
                        if (!_service.RequestWrite(devicePath, Vcp.AudioSpeakerVolume, requested))
                            NotifyRebuilding();
                        _api.ChangeQuery(view, true);
                        return false;
                    }));
            }
        }

        // Steps compound from the newest intended value, not the (possibly
        // stale) read-back — rapid presses must accumulate, not merge.
        var stepBase = m.PendingVolume is { Phase: OpPhase.Pending } p ? p.Target : m.CurrentVolume;
        if (stepBase.HasValue && m.VolumeMax > 0)
        {
            var up = Math.Min(stepBase.Value + VolumeStep, m.VolumeMax);
            var down = stepBase.Value >= VolumeStep ? stepBase.Value - VolumeStep : 0;

            results.Add(StepItem($"Volume +{VolumeStep}", Vcp.AudioSpeakerVolume, up, devicePath, view, IconVolume, 90));
            results.Add(StepItem($"Volume -{VolumeStep}", Vcp.AudioSpeakerVolume, down, devicePath, view, IconVolume, 80));
        }

        results.Add(BackItem($"{prefix}{m.Index} "));
        return results;
    }

    private List<Result> BuildBrightnessMenu(MonitorSnapshot m, string valueToken, string prefix)
    {
        if (m.CapsState != CapsState.Ready || !m.SupportsBrightness)
            return BuildMonitorMenu(m, prefix);

        var results = new List<Result>();
        var devicePath = m.DevicePath;
        var view = $"{prefix}{m.Index} bri ";

        results.Add(Item(
            m.CurrentBrightness.HasValue && m.BrightnessMax > 0
                ? $"Brightness: {m.CurrentBrightness} / {m.BrightnessMax}"
                : "Brightness: reading…",
            BrightnessStatus(m),
            IconBrightness, 100, view, _ => false));

        if (valueToken.Length > 0)
        {
            if (!uint.TryParse(valueToken, out var requested))
            {
                results.Add(Item(
                    $"Not a number: '{valueToken}'",
                    "Type a brightness value, e.g. 70.",
                    IconError, 200, view, _ => false));
            }
            else if (m.BrightnessMax == 0)
            {
                // Never send an unvalidated value to the hardware: until the
                // monitor's maximum has been read there is no range to check.
                results.Add(Item(
                    "Brightness range not read yet",
                    "Try again in a moment — the monitor's maximum has not been read.",
                    IconWarning, 200, view, _ => false));
            }
            else if (requested > m.BrightnessMax)
            {
                results.Add(Item(
                    $"Out of range (0–{m.BrightnessMax})",
                    $"This monitor accepts brightness values up to {m.BrightnessMax}.",
                    IconError, 200, view, _ => false));
            }
            else
            {
                results.Add(Item(
                    $"Set brightness to {requested}",
                    $"Apply to {m.FriendlyName}.",
                    IconBrightness, 200, view,
                    _ =>
                    {
                        if (!_service.RequestWrite(devicePath, Vcp.Luminance, requested))
                            NotifyRebuilding();
                        _api.ChangeQuery(view, true);
                        return false;
                    }));
            }
        }

        // Steps compound from the newest intended value, not the (possibly
        // stale) read-back — rapid presses must accumulate, not merge.
        var stepBase = m.PendingBrightness is { Phase: OpPhase.Pending } p ? p.Target : m.CurrentBrightness;
        if (stepBase.HasValue && m.BrightnessMax > 0)
        {
            var up = Math.Min(stepBase.Value + BrightnessStep, m.BrightnessMax);
            var down = stepBase.Value >= BrightnessStep ? stepBase.Value - BrightnessStep : 0;

            results.Add(StepItem($"Brightness +{BrightnessStep}", Vcp.Luminance, up, devicePath, view, IconBrightness, 90));
            results.Add(StepItem($"Brightness -{BrightnessStep}", Vcp.Luminance, down, devicePath, view, IconBrightness, 80));
        }

        results.Add(BackItem($"{prefix}{m.Index} "));
        return results;
    }

    /// <summary>
    /// The support surface: it records every DDC/CI exchange to a file so a
    /// problem on someone else's hardware can be diagnosed from the trace
    /// instead of from a description. Reachable only by typing <c>debug</c>.
    /// </summary>
    private List<Result> BuildDebugMenu(string argument, string prefix)
    {
        var view = $"{prefix}debug ";
        var on = _diagnostics.Enabled;

        var wantOn = argument switch
        {
            _ when argument.Equals("on", StringComparison.OrdinalIgnoreCase) => true,
            _ when argument.Equals("off", StringComparison.OrdinalIgnoreCase) => false,
            _ => !on,
        };

        var results = new List<Result>
        {
            Item(
                on ? "Diagnostics are on" : "Diagnostics are off",
                on
                    ? $"{_diagnostics.Written} events → {_diagnostics.FilePath}"
                        + (_diagnostics.Dropped > 0 ? $"  ({_diagnostics.Dropped} dropped)" : "")
                    : "Records every DDC/CI exchange to a file. Off by default; costs nothing while off.",
                on ? IconWarning : IconMonitor,
                100, view, _ => false),
        };

        if (wantOn != on)
        {
            results.Add(Item(
                wantOn ? "Turn diagnostics on" : "Turn diagnostics off",
                wantOn
                    ? "Starts a new trace file and keeps it running across restarts until turned off."
                    : "Closes the current trace file. The file stays on disk.",
                IconMonitor, 90, view,
                _ =>
                {
                    _diagnostics.SetEnabled(wantOn);
                    _api.ChangeQuery(view, true);
                    return false;
                }));
        }
        else
        {
            results.Add(Item(
                wantOn ? "Already on" : "Already off",
                $"Type '{prefix}debug {(wantOn ? "off" : "on")}' to change it.",
                IconMonitor, 90, view, _ => false));
        }

        var directory = _diagnostics.DirectoryPath;
        results.Add(Item(
            "Open the diagnostics folder",
            directory,
            IconMonitor, 80, view,
            _ =>
            {
                _api.OpenDirectory(directory);
                _onWindowDismissed();
                return true;
            }));

        results.Add(BackItem(prefix));
        return results;
    }

    private Result StepItem(string title, byte code, uint target, string devicePath, string view, string icon, int score) => Item(
        title,
        $"→ {target}",
        icon,
        score,
        view,
        _ =>
        {
            if (!_service.RequestWrite(devicePath, code, target))
                NotifyRebuilding();
            _api.ChangeQuery(view, true);
            return false;
        });

    private Result BackItem(string toQuery) => Item(
        "← Back",
        "",
        IconBack,
        -100,
        toQuery,
        _ => { _api.ChangeQuery(toQuery, true); return false; });

    private void NotifyRebuilding()
    {
        try
        {
            _api.ShowMsg(
                "MonitorCowboy",
                "Monitor list is rebuilding — try again in a moment.",
                Path.Combine(_pluginDirectory, "Images", "warning.png"));
        }
        catch
        {
            // A missed notice is not worth failing the action path for.
        }
    }

    private static string StatusLine(MonitorSnapshot m)
    {
        switch (m.CapsState)
        {
            case CapsState.Pending:
                return "Reading capabilities…";
            case CapsState.Unsupported:
                return "DDC/CI not supported";
        }

        var parts = new List<string>(4);
        if (m.SupportsInput)
            parts.Add($"Input: {InputPart(m)}");
        if (m.SupportsBrightness)
            parts.Add($"Brightness: {BrightnessPart(m)}");
        if (m.SupportsVolume)
            parts.Add($"Volume: {VolumePart(m)}");
        if (parts.Count == 0)
            parts.Add("No controllable features");
        if (m.ValuesStale)
            parts.Add("stale");
        if (m.RefreshInFlight)
            parts.Add("updating…");

        return string.Join(" · ", parts);
    }

    private static string InputPart(MonitorSnapshot m)
    {
        if (m.PendingInput is { } p)
        {
            return p.Phase switch
            {
                OpPhase.Pending => $"applying… → {InputSourceNames.NameOf(p.Target)}",
                OpPhase.Unverified => $"{InputSourceNames.NameOf(p.Target)}? (unverified)",
                OpPhase.Failed => "set failed",
                OpPhase.Rejected => "did not switch",
                _ => Current(),
            };
        }

        return Current();

        string Current() => m.CurrentInput.HasValue ? InputSourceNames.NameOf(m.CurrentInput.Value) : "…";
    }

    private static string VolumePart(MonitorSnapshot m)
    {
        if (m.PendingVolume is { } p)
        {
            return p.Phase switch
            {
                OpPhase.Pending => $"applying… → {p.Target}",
                OpPhase.Unverified => $"{p.Target}? (unverified)",
                OpPhase.Failed => "set failed",
                OpPhase.Rejected => "rejected (unchanged)",
                _ => Current(),
            };
        }

        return Current();

        string Current() => m.CurrentVolume?.ToString() ?? "…";
    }

    private static string VolumeStatus(MonitorSnapshot m) => m.PendingVolume switch
    {
        { Phase: OpPhase.Pending } p => $"Applying… (target: {p.Target})",
        { Phase: OpPhase.Unverified } => "Sent — could not verify.",
        { Phase: OpPhase.Failed } => "Set failed — the monitor rejected the command.",
        { Phase: OpPhase.Rejected } => "Rejected — the monitor kept its previous value.",
        _ => "Type a value to set it, or use the step items below.",
    };

    private static string BrightnessPart(MonitorSnapshot m)
    {
        if (m.PendingBrightness is { } p)
        {
            return p.Phase switch
            {
                OpPhase.Pending => $"applying… → {p.Target}",
                OpPhase.Unverified => $"{p.Target}? (unverified)",
                OpPhase.Failed => "set failed",
                OpPhase.Rejected => "rejected (unchanged)",
                _ => Current(),
            };
        }

        return Current();

        string Current() => m.CurrentBrightness?.ToString() ?? "…";
    }

    private static string BrightnessStatus(MonitorSnapshot m) => m.PendingBrightness switch
    {
        { Phase: OpPhase.Pending } p => $"Applying… (target: {p.Target})",
        { Phase: OpPhase.Unverified } => "Sent — could not verify.",
        { Phase: OpPhase.Failed } => "Set failed — the monitor rejected the command.",
        { Phase: OpPhase.Rejected } => "Rejected — the monitor kept its previous value (possibly locked by HDR / picture mode).",
        _ => m.BrightnessRecentlyRejected
            ? "A recent write was rejected — brightness may be locked by HDR or a picture mode."
            : "Type a value to set it, or use the step items below.",
    };

    private Result Item(
        string title,
        string subtitle,
        string icon,
        int score,
        string autoComplete,
        Func<ActionContext, bool> action,
        object? contextData = null) => new()
    {
        Title = title,
        SubTitle = subtitle,
        IcoPath = icon,
        Score = score,
        Action = Guarded(action),
        AutoCompleteText = autoComplete,
        ContextData = contextData,
        AddSelectedCount = false,
    };

    /// <summary>
    /// The host awaits <c>Result.ExecuteAsync</c> with no exception boundary of
    /// its own, so anything escaping an action surfaces inside its command
    /// handling. Every action this factory builds is wrapped here, alongside
    /// the other per-result contracts: a failed action leaves the window open
    /// instead of taking the launcher down with it.
    /// </summary>
    private Func<ActionContext, bool> Guarded(Func<ActionContext, bool> action) => context =>
    {
        try
        {
            return action(context);
        }
        catch (Exception ex)
        {
            // Silence here would present as "Enter does nothing", with nothing
            // in the log the README sends people to read.
            try
            {
                _api.LogException(nameof(ResultFactory), "Result action failed", ex);

                // Should never appear in a trace; if it does, it is the direct
                // evidence that this boundary was needed.
                if (_trace.Enabled)
                    _trace.Write("--", "ACTION.GUARD", $"ex={ex.GetType().Name} msg=\"{ex.Message}\"");
            }
            catch
            {
                // Logging must never take the action path down either.
            }

            return false;
        }
    };
}
