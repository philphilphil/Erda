namespace Erda.Server.Api;

/// <summary>
/// One editable config knob: its key, label/hint, the value to prefill the input with
/// (<c>Value</c> = pending override if any, else <c>Effective</c>), the currently-running
/// <c>Effective</c> value, whether a saved override exists (pending a restart), and the
/// <c>Group</c> the panel renders it under.
/// </summary>
public sealed record ConfigItemDto(string Key, string Label, string Hint, string? Value, string? Effective, bool Overridden, string Group);

/// <summary>Request to set/clear config overrides. A null/blank value clears that key's override.</summary>
public sealed record ConfigUpdateRequest(IReadOnlyDictionary<string, string?>? Values);
