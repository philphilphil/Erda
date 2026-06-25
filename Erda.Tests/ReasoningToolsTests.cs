using System.Text.Json;
using Erda.Agents.Tools;
using Erda.Core.Configuration;
using Erda.Core.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Erda.Tests;

public class ReasoningToolsTests
{
    private static AIFunction Tool(ReasoningTools tools, string name) =>
        (AIFunction)tools.AsTools().Single(t => ((AIFunction)t).Name == name);

    [Fact]
    public void Exposes_consult_codex_and_delegate_vault_task()
    {
        var names = MakeTools(out _).AsTools().Select(t => ((AIFunction)t).Name).ToList();
        Assert.Contains("consult_codex", names);
        Assert.Contains("delegate_vault_task", names);
    }

    [Fact]
    public async Task Delegate_vault_task_always_runs_codex_at_high_effort()
    {
        var tools = MakeTools(out _);

        var result = ((JsonElement)(await Tool(tools, "delegate_vault_task")
            .InvokeAsync(new() { ["task"] = "review my notes" }))!).GetString()!;

        // The fake codex dumps its argv, so we can see the effort the tool asked codex to run at.
        Assert.Contains("model_reasoning_effort=\"high\"", result);
    }

    /// <summary>A ReasoningTools wired to a CodexRunner whose 'codex' is a fake that dumps its argv
    /// into the -o file, so a test can read back exactly how codex was invoked.</summary>
    private static ReasoningTools MakeTools(out string vault)
    {
        vault = Directory.CreateTempSubdirectory("erda-test-vault-").FullName;
        var runner = new CodexRunner(
            Options.Create(new ErdaOptions
            {
                CodexExecutable = WriteArgvDumpCodex(),
                CodexTimeout = TimeSpan.FromSeconds(10),
                VaultPath = vault,
            }),
            NullLogger<CodexRunner>.Instance);
        return new ReasoningTools(runner);
    }

    /// <summary>Fake codex: drain stdin, then dump full argv (one arg per line) into the -o file.</summary>
    private static string WriteArgvDumpCodex()
    {
        var path = Path.Combine(Path.GetTempPath(), "argv-codex-" + Guid.NewGuid().ToString("N") + ".sh");
        File.WriteAllText(path,
            "#!/bin/bash\n" +
            "cat >/dev/null\n" +
            "out=\"\"; prev=\"\"\n" +
            "for a in \"$@\"; do [ \"$prev\" = \"-o\" ] && out=\"$a\"; prev=\"$a\"; done\n" +
            "[ -n \"$out\" ] && printf '%s\\n' \"$@\" > \"$out\"\n" +
            "exit 0\n");
        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }
}
