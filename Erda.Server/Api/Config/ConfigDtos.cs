namespace Erda.Server.Api;

/// <summary>
/// One row of effective configuration for the read-only Config screen: the <c>Group</c> it renders
/// under, a human <c>Label</c>, and the loaded <c>Value</c> (secrets shown as "(set)"/"(not set)").
/// </summary>
public sealed record ConfigItemDto(string Group, string Label, string Value);
