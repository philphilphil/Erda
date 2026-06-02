namespace Erda.Server.Api;

/// <summary>Login credentials. <c>Username</c> is optional (only checked when configured).</summary>
public sealed record LoginRequest(string? Username, string? Password);

/// <summary>Whether the panel requires auth, and whether the caller is currently authenticated.</summary>
public sealed record AuthState(bool AuthRequired, bool Authenticated);
