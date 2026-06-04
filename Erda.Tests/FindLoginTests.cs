using Erda.Agents.Tools;
using Erda.Core.Services.OnePassword;
using Xunit;

namespace Erda.Tests;

public class FindLoginTests
{
    private const string ListJson = """
    [
      { "id": "moxid", "title": "Moxfield", "category": "LOGIN",
        "urls": [ { "label": "website", "primary": true, "href": "https://www.moxfield.com" } ] },
      { "id": "ghid", "title": "GitHub", "category": "LOGIN",
        "urls": [ { "primary": true, "href": "https://github.com" } ] },
      { "id": "noteid", "title": "A secure note", "category": "SECURE_NOTE" }
    ]
    """;

    private const string MoxItemJson = """
    {
      "id": "moxid", "title": "Moxfield",
      "urls": [ { "primary": true, "href": "https://www.moxfield.com" } ],
      "fields": [
        { "id": "username", "type": "STRING", "label": "username", "value": "phil" },
        { "id": "password", "type": "CONCEALED", "label": "password", "value": "x" },
        { "id": "TOTP_abc", "type": "OTP", "label": "one-time password", "totp": "123456" }
      ]
    }
    """;

    [Fact]
    public void ParseList_reads_id_title_and_urls()
    {
        var items = FindLogin.ParseList(ListJson);
        Assert.Equal(3, items.Count);
        var mox = items[0];
        Assert.Equal("moxid", mox.Id);
        Assert.Equal("Moxfield", mox.Title);
        Assert.Equal("https://www.moxfield.com", Assert.Single(mox.Urls));
        Assert.Empty(items[2].Urls);                       // the secure note has no urls
    }

    [Fact]
    public void ParseItem_detects_a_totp_field()
    {
        var detail = FindLogin.ParseItem(MoxItemJson);
        Assert.Equal("moxid", detail.Id);
        Assert.True(detail.HasTotp);
    }

    [Fact]
    public void Match_finds_the_item_by_registrable_domain_via_subdomain()
    {
        var items = FindLogin.ParseList(ListJson);
        var hits = FindLogin.Match("https://login.moxfield.com/account", items);
        Assert.Equal("moxid", Assert.Single(hits).Id);
    }

    [Fact]
    public void Match_returns_empty_when_no_item_matches()
        => Assert.Empty(FindLogin.Match("https://example.org", FindLogin.ParseList(ListJson)));

    [Fact]
    public void BuildReferences_emits_only_references_and_includes_totp_when_present()
    {
        var detail = FindLogin.ParseItem(MoxItemJson);
        var refs = FindLogin.BuildReferences("Erda", detail);

        Assert.Equal("op://Erda/moxid/username", refs.UsernameRef);
        Assert.Equal("op://Erda/moxid/password", refs.PasswordRef);
        Assert.Equal("op://Erda/moxid/one-time password", refs.OneTimePasswordRef);
        // Nothing in the references is a secret value.
        Assert.DoesNotContain("123456", refs.UsernameRef + refs.PasswordRef + refs.OneTimePasswordRef);
    }

    [Fact]
    public void BuildReferences_omits_totp_when_the_item_has_none()
    {
        var noTotp = new OpItemDetail("id1", "Site", ["https://site.com"], HasTotp: false);
        Assert.Null(FindLogin.BuildReferences("Erda", noTotp).OneTimePasswordRef);
    }

    // ---- the tool end-to-end against a fake op CLI ----

    private sealed class FakeOpCli(string list, string item) : IOpCli
    {
        public Task<string> RunAsync(IReadOnlyList<string> args, CancellationToken ct = default) =>
            Task.FromResult(args is ["item", "list", ..] ? list : item);
    }

    [Fact]
    public async Task Tool_returns_references_for_a_single_match_and_never_a_value()
    {
        var tool = FindLogin.CreateTool(new FakeOpCli(ListJson, MoxItemJson), "Erda");
        var result = (string)(await tool.InvokeAsync(new() { ["domain"] = "moxfield.com" }))!;

        Assert.Contains("op://Erda/moxid/username", result);
        Assert.Contains("op://Erda/moxid/password", result);
        Assert.Contains("op://Erda/moxid/one-time password", result);
        Assert.DoesNotContain("123456", result);           // no TOTP value leaks
        Assert.DoesNotContain("\"value\"", result);
    }

    [Fact]
    public async Task Tool_reports_no_login_when_nothing_matches()
    {
        var tool = FindLogin.CreateTool(new FakeOpCli(ListJson, MoxItemJson), "Erda");
        var result = (string)(await tool.InvokeAsync(new() { ["domain"] = "unknown-site.com" }))!;
        Assert.Contains("No login", result);
    }
}
