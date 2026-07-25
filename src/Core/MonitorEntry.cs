namespace MonitorCowboy.Core;

/// <summary>
/// Mutable per-monitor state, guarded by its own lock. Written by the monitor's
/// DdcWorker and the owning service; read by the query path as immutable
/// <see cref="MonitorSnapshot"/>s only.
/// </summary>
public sealed class MonitorEntry
{
    private readonly object _gate = new();
    private readonly Action<MonitorEntry> _onChanged;

    private CapsState _capsState;
    private ParsedCapabilities? _caps;
    private uint? _currentInput;
    private uint? _currentVolume;
    private uint _volumeMax;
    private bool _valuesStale;
    private bool _refreshInFlight;
    private DateTime _lastValuesReadUtc = DateTime.MinValue;
    private PendingWrite? _pendingInput;
    private PendingWrite? _pendingVolume;

    public MonitorEntry(int index, nint handle, string devicePath, string friendlyName, Action<MonitorEntry> onChanged)
    {
        Index = index;
        Handle = handle;
        DevicePath = devicePath;
        FriendlyName = friendlyName;
        _onChanged = onChanged;
        _capsState = CapsState.Pending;
    }

    public int Index { get; }
    public nint Handle { get; }
    public string DevicePath { get; }
    public string FriendlyName { get; }

    public CapsState CapsState { get { lock (_gate) return _capsState; } }
    public bool SupportsInput { get { lock (_gate) return _caps?.Supports(Vcp.InputSource) ?? false; } }
    public bool SupportsVolume { get { lock (_gate) return _caps?.Supports(Vcp.AudioSpeakerVolume) ?? false; } }
    public DateTime LastValuesReadUtc { get { lock (_gate) return _lastValuesReadUtc; } }

    /// <summary>
    /// <paramref name="notify"/> is false only when seeding from the persisted
    /// cache during a rebuild — the monitor list is unpublished at that point,
    /// and a change event would push-refresh the UI against an empty list.
    /// </summary>
    public void ApplyCapabilities(ParsedCapabilities caps, bool notify = true)
    {
        lock (_gate)
        {
            _caps = caps;
            _capsState = CapsState.Ready;
        }
        if (notify)
            _onChanged(this);
    }

    public void ResetCapsPending()
    {
        lock (_gate)
        {
            // Keep the current map: a failed re-read must be able to fall back
            // to the known-good capabilities instead of discarding them.
            _capsState = CapsState.Pending;
        }
        _onChanged(this);
    }

    /// <summary>
    /// A capabilities read failed. A monitor we have never successfully read
    /// becomes Unsupported; one with a known-good map keeps it — a transient
    /// glitch (sleep, busy bus) must not demote a working monitor.
    /// </summary>
    public void MarkCapsReadFailed()
    {
        lock (_gate)
            _capsState = _caps is not null ? CapsState.Ready : CapsState.Unsupported;
        _onChanged(this);
    }

    /// <summary>Marks a refresh as queued; returns false when one is already in flight.</summary>
    public bool TryBeginRefresh()
    {
        lock (_gate)
        {
            if (_refreshInFlight)
                return false;
            _refreshInFlight = true;
            return true;
        }
    }

    public void ApplyReadValue(byte code, uint current, uint max)
    {
        lock (_gate)
        {
            if (code == Vcp.InputSource)
            {
                _currentInput = current;
                // A successful read supersedes a finished (non-pending) write outcome.
                if (_pendingInput is { Phase: not OpPhase.Pending })
                    _pendingInput = null;
            }
            else if (code == Vcp.AudioSpeakerVolume)
            {
                _currentVolume = current;
                if (max > 0)
                    _volumeMax = max;
                if (_pendingVolume is { Phase: not OpPhase.Pending })
                    _pendingVolume = null;
            }

            _valuesStale = false;
            _lastValuesReadUtc = DateTime.UtcNow;
        }
        _onChanged(this);
    }

    public void EndRefresh(bool anyReadFailed)
    {
        lock (_gate)
        {
            _refreshInFlight = false;
            if (anyReadFailed)
                _valuesStale = true;
            _lastValuesReadUtc = DateTime.UtcNow;
        }
        _onChanged(this);
    }

    /// <summary>Clears the refresh flag without touching the TTL timestamp (no read actually ran).</summary>
    public void CancelRefresh()
    {
        lock (_gate)
            _refreshInFlight = false;
        _onChanged(this);
    }

    public void SetPendingWrite(byte code, PendingWrite? pending)
    {
        lock (_gate)
        {
            if (code == Vcp.InputSource)
                _pendingInput = pending;
            else if (code == Vcp.AudioSpeakerVolume)
                _pendingVolume = pending;
        }
        _onChanged(this);
    }

    /// <summary>
    /// Records the outcome of a finished write, but only while the marker still
    /// belongs to that write — a newer queued write's Pending badge must not be
    /// overwritten by an older write's outcome.
    /// </summary>
    public void FinishPendingWrite(byte code, uint target, PendingWrite? outcome)
    {
        var changed = false;
        lock (_gate)
        {
            if (code == Vcp.InputSource)
            {
                if (_pendingInput is { Phase: OpPhase.Pending } p && p.Target == target)
                {
                    _pendingInput = outcome;
                    changed = true;
                }
            }
            else if (code == Vcp.AudioSpeakerVolume)
            {
                if (_pendingVolume is { Phase: OpPhase.Pending } p && p.Target == target)
                {
                    _pendingVolume = outcome;
                    changed = true;
                }
            }
        }
        if (changed)
            _onChanged(this);
    }

    public IReadOnlyList<uint> InputValues
    {
        get { lock (_gate) return _caps?.ValuesFor(Vcp.InputSource) ?? []; }
    }

    public MonitorSnapshot BuildSnapshot()
    {
        lock (_gate)
        {
            return new MonitorSnapshot
            {
                Index = Index,
                DevicePath = DevicePath,
                FriendlyName = FriendlyName,
                CapsState = _capsState,
                SupportsInput = _caps?.Supports(Vcp.InputSource) ?? false,
                SupportsVolume = _caps?.Supports(Vcp.AudioSpeakerVolume) ?? false,
                InputValues = _caps?.ValuesFor(Vcp.InputSource) ?? [],
                CurrentInput = _currentInput,
                CurrentVolume = _currentVolume,
                VolumeMax = _volumeMax,
                ValuesStale = _valuesStale,
                RefreshInFlight = _refreshInFlight,
                PendingInput = _pendingInput,
                PendingVolume = _pendingVolume,
            };
        }
    }
}
