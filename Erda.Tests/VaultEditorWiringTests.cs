using Erda.Agents.Tools;
using Erda.Core.Configuration;
using Erda.Core.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Erda.Tests;

public class VaultEditorWiringTests
{
    private static VaultService MakeVault()
    {
        var dir = Path.Combine(Path.GetTempPath(), "erda-wiring-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return new VaultService(Options.Create(new ErdaOptions { VaultPath = dir }));
    }

    private static List<string> ObsidianToolNames() =>
        new ObsidianTools(MakeVault()).AsTools().Select(t => ((AIFunction)t).Name).ToList();

    [Fact]
    public void ObsidianTools_no_longer_exposes_write_or_append()
    {
        var names = ObsidianToolNames();
        Assert.DoesNotContain("write_note", names);
        Assert.DoesNotContain("append_note", names);
    }

    [Fact]
    public void ObsidianTools_still_exposes_the_read_side_and_add_todo()
    {
        var names = ObsidianToolNames();
        Assert.Contains("list_notes", names);
        Assert.Contains("read_note", names);
        Assert.Contains("search_notes", names);
        Assert.Contains("add_todo", names);
    }

    private static VaultEditorTool MakeEditor() =>
        new(
            MakeVault(),
            Options.Create(new ErdaOptions
            {
                VaultPath = Path.GetTempPath(),
                ChatBaseUrl = "http://localhost:1234/v1",
                ChatModel = "gpt-5.5",
                ChatApiKey = "local",
            }),
            Options.Create(new ObservabilityOptions()),
            new FakeActivityRecorder(),
            NullLogger<VaultEditorTool>.Instance);

    [Fact]
    public void VaultEditorTool_exposes_a_single_edit_vault_note_tool()
    {
        var tool = MakeEditor().AsTool();
        Assert.Equal("edit_vault_note", ((AIFunction)tool).Name);
    }

    [Fact]
    public void SubAgent_is_built_with_the_editor_tool_set_and_web_search()
    {
        var tools = MakeEditor().BuildSubAgentTools();

        var functionNames = tools.OfType<AIFunction>().Select(f => f.Name).ToList();
        Assert.Equal(
            new[] { "read_note", "search_notes", "edit_note", "write_note" }.OrderBy(n => n),
            functionNames.OrderBy(n => n));
        Assert.Contains(tools, t => t is HostedWebSearchTool);
    }

    [Fact]
    public void SubAgent_excludes_reminder_notify_and_browser_tools()
    {
        var names = MakeEditor().BuildSubAgentTools().OfType<AIFunction>().Select(f => f.Name).ToList();

        Assert.DoesNotContain("add_todo", names);
        Assert.DoesNotContain("list_notes", names);
        foreach (var name in names)
        {
            Assert.DoesNotContain("remind", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("notify", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("browser", name, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void SubAgent_reasoning_effort_is_high()
    {
        Assert.Equal("high", VaultEditorTool.SubAgentReasoningEffort);
    }

    [Fact]
    public void SubAgent_instructions_pin_the_Erda_author_name_ahead_of_the_conventions()
    {
        var dir = Path.Combine(Path.GetTempPath(), "erda-pin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "AGENTS.md"), "Choose an honest agent name (Codex/Claude/GPT/Gemini).");

        var editor = new VaultEditorTool(
            new VaultService(Options.Create(new ErdaOptions { VaultPath = dir })),
            Options.Create(new ErdaOptions
            {
                VaultPath = dir,
                ChatBaseUrl = "http://localhost:1234/v1",
                ChatModel = "gpt-5.5",
                ChatApiKey = "local",
            }),
            Options.Create(new ObservabilityOptions()),
            new FakeActivityRecorder(),
            NullLogger<VaultEditorTool>.Instance);

        var instr = editor.BuildSubAgentInstructions("Note.md");

        Assert.Equal("Erda", VaultEditorTool.AuthorName);
        Assert.Contains("author=\"Erda\"", instr);                       // the pin is present
        Assert.Contains("Choose an honest agent name", instr);          // the stacked conventions still follow
        // The fixed pin precedes (and so overrides) the conventions' name-choice guidance.
        Assert.True(instr.IndexOf("author=\"Erda\"", StringComparison.Ordinal)
                  < instr.IndexOf("Choose an honest agent name", StringComparison.Ordinal));
    }
}
