namespace Erda.Server.Api;

/// <summary>A simple error payload: <c>{ "error": "…" }</c>.</summary>
public sealed record ErrorResponse(string Error);
