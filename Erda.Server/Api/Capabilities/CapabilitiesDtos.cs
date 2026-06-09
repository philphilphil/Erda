namespace Erda.Server.Api.Capabilities;

public sealed record McpToolDto(string Name, string? Description);
public sealed record McpServerDto(string Name, string Transport, bool Connected, IReadOnlyList<McpToolDto> Tools);
public sealed record McpCapabilitiesResponse(IReadOnlyList<McpServerDto> Servers);

public sealed record AccountDto(string Title, IReadOnlyList<string> Sites);
public sealed record AccountsResponse(IReadOnlyList<AccountDto> Accounts);
