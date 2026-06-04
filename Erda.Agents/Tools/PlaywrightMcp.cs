using Erda.Core.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Client;

namespace Erda.Agents.Tools;

/// <summary>
/// Real <see cref="IBrowserMcp"/>: launches <c>npx @playwright/mcp</c> over stdio and lists its tools.
/// Connect is best-effort and idempotent; any failure is logged and leaves the server "not connected"
/// (empty tools), so the rest of the app starts normally and the panel shows it as down.
/// </summary>
public sealed class PlaywrightMcp(
    IOptions<BrowserOptions> options,
    ILoggerFactory loggerFactory) : IBrowserMcp, IAsyncDisposable
{
    private const string ServerName = "playwright";

    private readonly BrowserOptions _opts = options.Value;
    private readonly ILogger<PlaywrightMcp> _logger = loggerFactory.CreateLogger<PlaywrightMcp>();
    // Not disposed: this is an app-lifetime singleton, so the semaphore lives until process exit.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private McpClient? _client;
    private volatile IReadOnlyList<AITool> _tools = [];
    private volatile bool _connected;

    public bool Enabled => _opts.Enabled;
    public IReadOnlyList<AITool> Tools => _tools;

    public McpServerStatus Status => new(
        Name: ServerName,
        Transport: "stdio",
        Connected: _connected,
        Tools: _tools.Select(t => new McpToolInfo(t.Name, (t as AIFunction)?.Description)).ToList());

    public async Task EnsureStartedAsync(CancellationToken cancellationToken = default)
    {
        if (!_opts.Enabled || _connected) return;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_connected) return;

            // Always include the user-data-dir + output-dir from config (single source of truth), then
            // append --headless when configured, so local dev can watch the browser window.
            var args = new List<string>(_opts.McpArgs)
            {
                "--user-data-dir", _opts.UserDataDir,
                "--output-dir", _opts.OutputDir,
            };
            if (_opts.Headless) args.Add("--headless");

            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = ServerName,
                Command = _opts.McpCommand,
                Arguments = args.ToArray(),
                ShutdownTimeout = TimeSpan.FromSeconds(10),
            }, loggerFactory);

            _client = await McpClient.CreateAsync(transport, null, loggerFactory, cancellationToken);
            IList<McpClientTool> tools = await _client.ListToolsAsync((RequestOptions?)null, cancellationToken);
            _tools = [.. tools]; // McpClientTool : AIFunction : AITool
            _connected = true;
            _logger.LogInformation("Playwright MCP connected: {Count} tools.", _tools.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Playwright MCP failed to connect; browse_web will be unavailable.");
            if (_client is not null) { await _client.DisposeAsync(); _client = null; }
            _tools = [];
            _connected = false;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null) await _client.DisposeAsync();
    }
}
