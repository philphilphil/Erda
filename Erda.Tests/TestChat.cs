using Erda.Agents.Services;
using Erda.Core.Configuration;
using Microsoft.Extensions.Options;

namespace Erda.Tests;

/// <summary>Builds a <see cref="ChatClientFactory"/> over dummy settings for tests that need one
/// constructed but never call the model.</summary>
internal static class TestChat
{
    public static ErdaOptions Options() => new()
    {
        VaultPath = Path.GetTempPath(),
        ChatBaseUrl = "http://localhost:1234/v1",
        ChatApiKey = "test-key",
        ChatModel = "claude-opus-5-5",
        ChatReasoningEffort = "medium",
        ChatHistoryMaxMessages = 60,
    };

    public static ChatClientFactory Factory() => new(Microsoft.Extensions.Options.Options.Create(Options()));
}
