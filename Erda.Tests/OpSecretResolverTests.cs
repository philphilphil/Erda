using Erda.Core.Configuration;
using Erda.Core.Services.OnePassword;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Erda.Tests;

public class OpSecretResolverTests
{
    /// <summary>Fake op CLI: records the argv it was called with and returns a canned stdout.</summary>
    private sealed class FakeOpCli(string stdout) : IOpCli
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];
        public Task<string> RunAsync(IReadOnlyList<string> args, CancellationToken ct = default)
        {
            Calls.Add(args);
            return Task.FromResult(stdout);
        }
    }

    private static OpSecretResolver Make(IOpCli cli, string vault = "Erda") =>
        new(cli, Options.Create(new BrowserOptions { OnePasswordVault = vault }), NullLogger<OpSecretResolver>.Instance);

    [Fact]
    public async Task Resolves_a_plain_field_via_op_read()
    {
        var cli = new FakeOpCli("s3cr3t-password");
        var value = await Make(cli).ResolveAsync("op://Erda/Moxfield/password");

        Assert.Equal("s3cr3t-password", value);
        var argv = Assert.Single(cli.Calls);
        Assert.Equal(["read", "op://Erda/Moxfield/password"], argv);
    }

    [Fact]
    public async Task Resolves_a_totp_field_via_op_item_get_otp()
    {
        var cli = new FakeOpCli("123456");
        var value = await Make(cli).ResolveAsync("op://Erda/Moxfield/one-time password");

        Assert.Equal("123456", value);
        var argv = Assert.Single(cli.Calls);
        Assert.Equal(["item", "get", "Moxfield", "--vault", "Erda", "--otp"], argv);
    }

    [Fact]
    public async Task Refuses_a_reference_outside_the_configured_vault()
    {
        var cli = new FakeOpCli("should-not-be-read");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Make(cli).ResolveAsync("op://Personal/Bank/password"));

        Assert.Empty(cli.Calls);                          // never shelled out
        Assert.DoesNotContain("should-not-be-read", ex.Message);
    }

    [Theory]
    [InlineData("not-a-reference")]
    [InlineData("op://Erda/Moxfield")]                     // missing field
    public async Task Rejects_malformed_references(string reference)
    {
        var cli = new FakeOpCli("x");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Make(cli).ResolveAsync(reference));
        Assert.Empty(cli.Calls);
    }

    [Fact]
    public async Task Throws_when_op_returns_an_empty_value()
    {
        var cli = new FakeOpCli("   ");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Make(cli).ResolveAsync("op://Erda/Moxfield/username"));
        Assert.Contains("op://Erda/Moxfield/username", ex.Message);   // names the ref, carries no value
    }
}
