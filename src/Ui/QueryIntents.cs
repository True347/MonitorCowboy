using MonitorCowboy.Core;

namespace MonitorCowboy.Ui;

/// <summary>Typed outcome of parsing the query text. Produced by <c>QueryRouter</c>, rendered by <c>ResultFactory</c>.</summary>
public abstract record QueryIntent;

/// <summary>L1: list all monitors, optionally filtered by a name substring (empty filter = all).</summary>
public sealed record MonitorListIntent(string Filter) : QueryIntent;

/// <summary>L2: the action menu (input / volume) of one monitor.</summary>
public sealed record MonitorMenuIntent(MonitorSnapshot Monitor) : QueryIntent;

/// <summary>L3a: the input source list of one monitor, optionally filtered.</summary>
public sealed record InputMenuIntent(MonitorSnapshot Monitor, string Filter) : QueryIntent;

/// <summary>
/// L3b: the volume view of one monitor. <paramref name="ValueToken"/> is the raw
/// token typed after "vol" ("" when absent); validation against VolumeMax happens
/// at render time.
/// </summary>
public sealed record VolumeMenuIntent(MonitorSnapshot Monitor, string ValueToken) : QueryIntent;

/// <summary>
/// The diagnostics view. Reached only by typing <c>debug</c>; it is deliberately
/// absent from the monitor list so that the everyday surface stays two items
/// wide. <paramref name="Argument"/> is the raw token after it ("on"/"off"/"").
/// </summary>
public sealed record DebugMenuIntent(string Argument) : QueryIntent;
