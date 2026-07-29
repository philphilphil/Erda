using Erda.Agents.Workflows;
using Erda.Core.Configuration;
using Erda.Core.Data;
using Erda.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Erda.Tests;

/// <summary>
/// Collision behaviour of the two inbox writers. Uses a real <see cref="VaultService"/> over a temp
/// directory — the bug being covered is a filesystem clobber, which a faked file IO would hide.
/// </summary>
public class MemoProcessorTests
{
    private static (MemoProcessor Processor, VaultService Vault, FakeReasoner Reasoner) Make()
    {
        var dir = Path.Combine(Path.GetTempPath(), "erda-vault-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var vault = new VaultService(Options.Create(new ErdaOptions { VaultPath = dir }));
        var reasoner = new FakeReasoner();
        var processor = new MemoProcessor(reasoner, vault, new PromptStore(TestDb.NewFactory()),
            NullLogger<MemoProcessor>.Instance);
        return (processor, vault, reasoner);
    }

    /// <summary>The leading "{date}_{time}" of an inbox note name, i.e. the part the two writers stamp.</summary>
    private static string Stamp(string relativePath)
    {
        var parts = Path.GetFileName(relativePath).Split('_');
        return parts[0] + "_" + parts[1];
    }

    [Fact]
    public async Task SaveRawAsync_writes_the_transcript_to_the_path_it_returns()
    {
        var (processor, vault, _) = Make();

        var path = await processor.SaveRawAsync("HELLO RAW");

        Assert.True(vault.Exists(path));
        Assert.StartsWith("1 Inbox/", path);
        Assert.EndsWith("_voice-memo-raw.md", path);
        var body = vault.ReadNote(path);
        Assert.Contains("# Voice memo (raw transcript)", body);
        Assert.Contains("HELLO RAW", body);
    }

    [Fact]
    public async Task SaveRawAsync_never_overwrites_a_raw_memo_saved_in_the_same_second()
    {
        var (processor, vault, _) = Make();

        // The stamp has second precision, so the collision path only runs when both saves land in the
        // same second — which back-to-back calls all but always do. Retrying across the rare second
        // rollover keeps the test deterministic instead of a once-in-a-few-thousand-runs flake.
        string first, second;
        var attempts = 0;
        do
        {
            first = await processor.SaveRawAsync("TRANSCRIPT ONE");
            second = await processor.SaveRawAsync("TRANSCRIPT TWO");
        }
        while (Stamp(first) != Stamp(second) && ++attempts < 20);

        Assert.Equal(Stamp(first), Stamp(second));  // same second — the collision really happened
        Assert.NotEqual(first, second);
        Assert.Contains("TRANSCRIPT ONE", vault.ReadNote(first));
        Assert.DoesNotContain("TRANSCRIPT TWO", vault.ReadNote(first));
        Assert.Contains("TRANSCRIPT TWO", vault.ReadNote(second));
        Assert.DoesNotContain("TRANSCRIPT ONE", vault.ReadNote(second));
    }

    [Fact]
    public async Task ProcessAsync_writes_the_note_to_the_path_it_returns()
    {
        var (processor, vault, reasoner) = Make();
        reasoner.Result = "# Weekly plan\n\nBODY\n";

        var result = await processor.ProcessAsync("some transcript");

        Assert.True(vault.Exists(result.NotePath));
        Assert.StartsWith("1 Inbox/", result.NotePath);
        Assert.EndsWith("_weekly-plan.md", result.NotePath);
        Assert.Equal("# Weekly plan\n\nBODY\n", vault.ReadNote(result.NotePath));
        Assert.Contains(result.NotePath, result.Reply);
    }

    [Fact]
    public async Task ProcessAsync_never_overwrites_a_note_with_the_same_slug_in_the_same_minute()
    {
        var (processor, vault, reasoner) = Make();

        // Same title ⇒ same slug; the stamp has minute precision, so back-to-back calls collide unless
        // a minute rolls over between them — retry in that case so the collision path is always covered.
        string first, second;
        var attempts = 0;
        do
        {
            reasoner.Result = "# Weekly plan\n\nBODY ONE\n";
            first = (await processor.ProcessAsync("t1")).NotePath;
            reasoner.Result = "# Weekly plan\n\nBODY TWO\n";
            second = (await processor.ProcessAsync("t2")).NotePath;
        }
        while (Stamp(first) != Stamp(second) && ++attempts < 20);

        Assert.Equal(Stamp(first), Stamp(second));  // same minute — the collision really happened
        Assert.NotEqual(first, second);
        Assert.Equal("# Weekly plan\n\nBODY ONE\n", vault.ReadNote(first));
        Assert.Equal("# Weekly plan\n\nBODY TWO\n", vault.ReadNote(second));
    }
}
