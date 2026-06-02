using System.Runtime.CompilerServices;

// Allow the test project to access internal members — used to call ChatEndpoints.StreamChatAsync
// directly so SSE-framing tests exercise the real production code rather than a duplicated copy.
[assembly: InternalsVisibleTo("Erda.Tests")]
