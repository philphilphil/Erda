using Erda.Core.Data;
using Xunit;

namespace Erda.Tests;

/// <summary>
/// Covers the kind-aware prompt store: the orchestrator's system prompt and the voice-memo prompt
/// share one versioned table but are fully independent — seeding, version lists, and rollback all
/// scope to a single <c>kind</c>. The DB is a throwaway SQLite file per test.
/// </summary>
public class PromptStoreTests
{
    private static PromptStore New() => new(TestDb.NewFactory());

    [Fact]
    public void GetActiveContent_seeds_the_code_default_per_kind()
    {
        var store = New();

        Assert.Equal("SYS-DEFAULT", store.GetActiveContent(PromptKind.System, "SYS-DEFAULT"));
        Assert.Equal("VOICE-DEFAULT", store.GetActiveContent(PromptKind.Voice, "VOICE-DEFAULT"));
    }

    [Fact]
    public void Saving_one_kind_leaves_the_other_kinds_active_content_untouched()
    {
        var store = New();
        store.GetActiveContent(PromptKind.System, "SYS-DEFAULT"); // seed system

        store.SaveNewVersion(PromptKind.Voice, "new voice prompt", null);

        // Reading again must not re-seed (an active row already exists for each kind).
        Assert.Equal("SYS-DEFAULT", store.GetActiveContent(PromptKind.System, "SHOULD-NOT-RESEED"));
        Assert.Equal("new voice prompt", store.GetActiveContent(PromptKind.Voice, "SHOULD-NOT-RESEED"));
    }

    [Fact]
    public void ListVersions_returns_only_the_requested_kind_newest_first()
    {
        var store = New();
        store.SaveNewVersion(PromptKind.System, "sys-1", "first");
        store.SaveNewVersion(PromptKind.Voice, "voice-1", "v");
        store.SaveNewVersion(PromptKind.System, "sys-2", "second");

        var sys = store.ListVersions(PromptKind.System);
        Assert.Equal(2, sys.Count);
        Assert.All(sys, v => Assert.Equal(PromptKind.System, v.Kind));
        Assert.Equal("sys-2", sys[0].Content); // newest first

        var voice = store.ListVersions(PromptKind.Voice);
        Assert.Single(voice);
        Assert.Equal("voice-1", voice[0].Content);
    }

    [Fact]
    public void Activate_only_moves_the_active_flag_within_the_same_kind()
    {
        var store = New();
        var sysV1 = store.SaveNewVersion(PromptKind.System, "sys-1", null);
        store.SaveNewVersion(PromptKind.System, "sys-2", null); // sys-2 is active
        store.SaveNewVersion(PromptKind.Voice, "voice-1", null); // voice active

        Assert.True(store.Activate(sysV1.Id)); // roll system back to v1

        Assert.Equal("sys-1", store.GetActiveContent(PromptKind.System, "x"));
        // The voice prompt's active row is untouched by a system rollback.
        Assert.Equal("voice-1", store.GetActiveContent(PromptKind.Voice, "x"));
        var voiceVersions = store.ListVersions(PromptKind.Voice);
        Assert.Single(voiceVersions);
        Assert.True(voiceVersions[0].IsActive);
    }
}
