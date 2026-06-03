using Erda.Core.Data;
using Xunit;

namespace Erda.Tests;

/// <summary>
/// Covers the kind-aware prompt store: the orchestrator's system prompt and the voice-memo prompt
/// share one versioned table but are fully independent — active content, version lists, and rollback
/// all scope to a single <c>kind</c>. A fresh DB starts empty (no code-baked seed). The DB is a
/// throwaway SQLite file per test.
/// </summary>
public class PromptStoreTests
{
    private static PromptStore New() => new(TestDb.NewFactory());

    [Fact]
    public void GetActiveContent_returns_null_when_no_version_saved()
    {
        var store = New();

        Assert.Null(store.GetActiveContent(PromptKind.System));
        Assert.Null(store.GetActiveContent(PromptKind.Voice));
    }

    [Fact]
    public void Saving_one_kind_leaves_the_other_kinds_active_content_untouched()
    {
        var store = New();
        store.SaveNewVersion(PromptKind.System, "sys prompt", null);

        store.SaveNewVersion(PromptKind.Voice, "new voice prompt", null);

        Assert.Equal("sys prompt", store.GetActiveContent(PromptKind.System));
        Assert.Equal("new voice prompt", store.GetActiveContent(PromptKind.Voice));
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

        Assert.Equal("sys-1", store.GetActiveContent(PromptKind.System));
        // The voice prompt's active row is untouched by a system rollback.
        Assert.Equal("voice-1", store.GetActiveContent(PromptKind.Voice));
        var voiceVersions = store.ListVersions(PromptKind.Voice);
        Assert.Single(voiceVersions);
        Assert.True(voiceVersions[0].IsActive);
    }
}
