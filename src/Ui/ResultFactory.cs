using Flow.Launcher.Plugin;
using MonitorCowboy.Core;

namespace MonitorCowboy.Ui;

/// <summary>
/// Renders a <see cref="QueryIntent"/> into Flow results, exclusively from
/// snapshots — never from live DDC/CI state. Every result sets
/// AddSelectedCount=false so Flow's selection history cannot reorder the list
/// (the numeric grammar depends on stable ordering).
/// </summary>
public sealed class ResultFactory
{
    private const string IconApp = "Images/app.png";
    private const string IconMonitor = "Images/monitor.png";
    private const string IconInput = "Images/input.png";
    private const string IconVolume = "Images/volume.png";
    private const string IconBack = "Images/back.png";
    private const string IconWarning = "Images/warning.png";
    private const string IconError = "Images/error.png";

    private const int VolumeStep = 5;

    private readonly IPublicAPI _api;
    private readonly MonitorService _service;

    public ResultFactory(IPublicAPI api, MonitorService service)
    {
        _api = api;
        _service = service;
    }

    public List<Result> Build(QueryIntent intent, IReadOnlyList<MonitorSnapshot> monitors, string actionKeyword)
    {
        var prefix = actionKeyword == "*" ? "" : actionKeyword + " ";

        return intent switch
        {
            MonitorListIntent list => BuildMonitorList(monitors, list.Filter, prefix),
            MonitorMenuIntent menu => BuildMonitorMenu(menu.Monitor, prefix),
            InputMenuIntent input => BuildInputMenu(input.Monitor, input.Filter, prefix),
            VolumeMenuIntent volume => BuildVolumeMenu(volume.Monitor, volume.ValueToken, prefix),
            _ => BuildMonitorList(monitors, "", prefix),
        };
    }

    public Result ErrorItem(string message) => Item(
        title: "MonitorCowboy error",
        subtitle: message,
        icon: IconError,
        score: 0,
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
                    IconWarning, 0, _ => false),
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
                100 - (m.Index - 1) * 10,
                _ => { _api.ChangeQuery(target, true); return false; },
                autoComplete: target,
                contextData: m.DevicePath));
        }

        return results;
    }

    private List<Result> BuildMonitorMenu(MonitorSnapshot m, string prefix)
    {
        var results = new List<Result>();

        switch (m.CapsState)
        {
            case CapsState.Pending:
                results.Add(Item(
                    "Reading capabilities…",
                    "First contact with this monitor can take a few seconds.",
                    IconMonitor, 50, _ => false, contextData: m.DevicePath));
                break;

            case CapsState.Unsupported:
                results.Add(Item(
                    "This monitor does not support DDC/CI control",
                    "Enable DDC/CI in the monitor's OSD menu if available, then re-read capabilities via the context menu (Shift+Enter).",
                    IconWarning, 50, _ => false, contextData: m.DevicePath));
                break;

            default:
                if (m.SupportsInput)
                {
                    var target = $"{prefix}{m.Index} in ";
                    results.Add(Item(
                        "Input source",
                        $"Current: {InputPart(m)}",
                        IconInput, 50,
                        _ => { _api.ChangeQuery(target, true); return false; },
                        autoComplete: target,
                        contextData: m.DevicePath));
                }

                if (m.SupportsVolume)
                {
                    var target = $"{prefix}{m.Index} vol ";
                    results.Add(Item(
                        "Volume",
                        $"Current: {VolumePart(m)}",
                        IconVolume, 40,
                        _ => { _api.ChangeQuery(target, true); return false; },
                        autoComplete: target,
                        contextData: m.DevicePath));
                }

                if (!m.SupportsInput && !m.SupportsVolume)
                {
                    results.Add(Item(
                        "No controllable features",
                        "This monitor's capabilities list neither input source (0x60) nor speaker volume (0x62).",
                        IconWarning, 50, _ => false, contextData: m.DevicePath));
                }

                break;
        }

        results.Add(BackItem(prefix));
        return results;
    }

    private List<Result> BuildInputMenu(MonitorSnapshot m, string filter, string prefix)
    {
        var results = new List<Result>();

        if (m.CapsState != CapsState.Ready || !m.SupportsInput)
            return BuildMonitorMenu(m, prefix);

        if (m.InputValues.Count == 0)
        {
            results.Add(Item(
                "Monitor reports no selectable inputs",
                "The capabilities string lists VCP 0x60 without any values.",
                IconWarning, 50, _ => false, contextData: m.DevicePath));
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
                    _ => subtitle,
                };
            }

            var devicePath = m.DevicePath;
            var refreshQuery = $"{prefix}{m.Index} in ";
            results.Add(Item(
                (isCurrent ? "✓ " : "") + name,
                subtitle,
                IconInput,
                score,
                _ =>
                {
                    _service.RequestWrite(devicePath, Vcp.InputSource, value);
                    _api.ChangeQuery(refreshQuery, true);
                    return false;
                },
                contextData: m.DevicePath));
            score -= 5;
        }

        results.Add(BackItem($"{prefix}{m.Index} "));
        return results;
    }

    private List<Result> BuildVolumeMenu(MonitorSnapshot m, string valueToken, string prefix)
    {
        var results = new List<Result>();

        if (m.CapsState != CapsState.Ready || !m.SupportsVolume)
            return BuildMonitorMenu(m, prefix);

        var devicePath = m.DevicePath;
        var refreshQuery = $"{prefix}{m.Index} vol ";

        results.Add(Item(
            m.CurrentVolume.HasValue && m.VolumeMax > 0
                ? $"Volume: {m.CurrentVolume} / {m.VolumeMax}"
                : "Volume: reading…",
            VolumeStatus(m),
            IconVolume, 100, _ => false, contextData: m.DevicePath));

        if (valueToken.Length > 0)
        {
            if (!uint.TryParse(valueToken, out var requested))
            {
                results.Add(Item(
                    $"Not a number: '{valueToken}'",
                    "Type a volume value, e.g. 30.",
                    IconError, 200, _ => false));
            }
            else if (m.VolumeMax > 0 && requested > m.VolumeMax)
            {
                results.Add(Item(
                    $"Out of range (0–{m.VolumeMax})",
                    $"This monitor accepts volume values up to {m.VolumeMax}.",
                    IconError, 200, _ => false));
            }
            else
            {
                results.Add(Item(
                    $"Set volume to {requested}",
                    $"Apply to {m.FriendlyName}.",
                    IconVolume, 200,
                    _ =>
                    {
                        _service.RequestWrite(devicePath, Vcp.AudioSpeakerVolume, requested);
                        _api.ChangeQuery(refreshQuery, true);
                        return false;
                    }));
            }
        }

        if (m.CurrentVolume.HasValue && m.VolumeMax > 0)
        {
            var current = m.CurrentVolume.Value;
            var up = Math.Min(current + VolumeStep, m.VolumeMax);
            var down = current >= VolumeStep ? current - VolumeStep : 0;

            results.Add(StepItem($"Volume +{VolumeStep}", up, devicePath, refreshQuery, 90));
            results.Add(StepItem($"Volume -{VolumeStep}", down, devicePath, refreshQuery, 80));
        }

        results.Add(BackItem($"{prefix}{m.Index} "));
        return results;
    }

    private Result StepItem(string title, uint target, string devicePath, string refreshQuery, int score) => Item(
        title,
        $"→ {target}",
        IconVolume,
        score,
        _ =>
        {
            _service.RequestWrite(devicePath, Vcp.AudioSpeakerVolume, target);
            _api.ChangeQuery(refreshQuery, true);
            return false;
        });

    private Result BackItem(string toQuery) => Item(
        "← Back",
        "",
        IconBack,
        -100,
        _ => { _api.ChangeQuery(toQuery, true); return false; },
        autoComplete: toQuery);

    private static string StatusLine(MonitorSnapshot m)
    {
        switch (m.CapsState)
        {
            case CapsState.Pending:
                return "Reading capabilities…";
            case CapsState.Unsupported:
                return "DDC/CI not supported";
        }

        var parts = new List<string>(3);
        if (m.SupportsInput)
            parts.Add($"Input: {InputPart(m)}");
        if (m.SupportsVolume)
            parts.Add($"Volume: {VolumePart(m)}");
        if (parts.Count == 0)
            parts.Add("No input/volume controls");
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
        _ => "Type a value to set it, or use the step items below.",
    };

    private static Result Item(
        string title,
        string subtitle,
        string icon,
        int score,
        Func<ActionContext, bool> action,
        string? autoComplete = null,
        object? contextData = null) => new()
    {
        Title = title,
        SubTitle = subtitle,
        IcoPath = icon,
        Score = score,
        Action = action,
        AutoCompleteText = autoComplete,
        ContextData = contextData,
        AddSelectedCount = false,
    };
}
