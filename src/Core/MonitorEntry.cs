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

    public void ApplyCapabilities(ParsedCapabilities caps)
    {
        lock (_gate)
        {
            _caps = caps;
            _capsState = CapsState.Ready;
        }
        _onChanged(this);
    }

    public void MarkCapsUnsupported()
    {
        lock (_gate)
        {
            _caps = null;
            _capsState = CapsState.Unsupported;
        }
        _onChanged(this);
    }

    public void ResetCapsPending()
    {
        lock (_gate)
        {
            _caps = null;
            _capsState = CapsState.Pending;
        }
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
