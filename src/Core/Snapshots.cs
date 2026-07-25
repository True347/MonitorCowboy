namespace MonitorCowboy.Core;

/// <summary>Lifecycle of the per-monitor capabilities read.</summary>
public enum CapsState
{
    /// <summary>Capabilities read still running (first sight of this monitor; can take seconds).</summary>
    Pending,

    /// <summary>Capabilities parsed successfully.</summary>
    Ready,

    /// <summary>Capabilities could not be read or contained no usable VCP section - DDC/CI unsupported.</summary>
    Unsupported,
}

/// <summary>Outcome phases of an in-flight or finished VCP write (§ verify-after-set).</summary>
public enum OpPhase
{
    /// <summary>Enqueued or executing; final outcome unknown.</summary>
    Pending,

    /// <summary>Set succeeded and the read-back matched the target.</summary>
    Applied,

    /// <summary>Set was sent but the read-back failed or mismatched (common right after switching inputs away).</summary>
    Unverified,

    /// <summary>The set call itself failed.</summary>
    Failed,
}

/// <summary>An in-flight (or just-finished) write against a single VCP code.</summary>
public sealed record PendingWrite(uint Target, OpPhase Phase);

/// <summary>
/// Immutable view of one physical monitor. The query path renders exclusively
/// from snapshots - it never performs DDC/CI I/O.
/// </summary>
public sealed record MonitorSnapshot
{
    /// <summary>1-based position in the monitor list; stable for the session.</summary>
    public required int Index { get; init; }

    /// <summary>Device instance path from DISPLAYCONFIG_TARGET_DEVICE_NAME; primary identity/cache key.</summary>
    public required string DevicePath { get; init; }

    /// <summary>Human-readable model name, e.g. "DELL U2723QE"; display only.</summary>
    public required string FriendlyName { get; init; }

    public required CapsState CapsState { get; init; }

    /// <summary>True when the capabilities string lists VCP 0x60.</summary>
    public bool SupportsInput { get; init; }

    /// <summary>True when the capabilities string lists VCP 0x62.</summary>
    public bool SupportsVolume { get; init; }

    /// <summary>Raw 0x60 values accepted by this monitor, in capabilities order.</summary>
    public IReadOnlyList<uint> InputValues { get; init; } = [];

    /// <summary>Last read 0x60 value (raw, unmasked); null when unknown/unsupported.</summary>
    public uint? CurrentInput { get; init; }

    /// <summary>Last read 0x62 value; null when unknown/unsupported.</summary>
    public uint? CurrentVolume { get; init; }

    /// <summary>Maximum for 0x62 as reported by the monitor; 0 while unknown.</summary>
    public uint VolumeMax { get; init; }

    /// <summary>True when the last read attempt failed (asleep/off/NAK); values may be outdated.</summary>
    public bool ValuesStale { get; init; }

    /// <summary>True while a value refresh is queued or running for this monitor.</summary>
    public bool RefreshInFlight { get; init; }

    public PendingWrite? PendingInput { get; init; }

    public PendingWrite? PendingVolume { get; init; }
}
