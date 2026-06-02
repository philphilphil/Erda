namespace Erda.Core.Data;

/// <summary>
/// A configuration override edited in the panel. <see cref="Key"/> is in ASP.NET
/// <c>Section:Key</c> form (e.g. <c>ErrorWatch:MinLevel</c>); loaded at startup by the SQLite
/// configuration provider, layered over appsettings/env. Applied on restart (v1).
/// </summary>
public sealed class ConfigOverride
{
    public string Key { get; set; } = "";
    public string? Value { get; set; }
}
