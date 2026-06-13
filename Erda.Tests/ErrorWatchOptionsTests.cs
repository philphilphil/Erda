using Erda.Core.Configuration;
using Xunit;

namespace Erda.Tests;

public class ErrorWatchOptionsTests
{
    [Fact]
    public void SignaturePropertyNames_splits_trims_and_drops_empties()
    {
        var opts = new ErrorWatchOptions { SignatureProperties = " venue , error ,, " };
        Assert.Equal(new[] { "venue", "error" }, opts.SignaturePropertyNames);
    }

    [Fact]
    public void SignaturePropertyNames_is_empty_when_unset()
    {
        Assert.Empty(new ErrorWatchOptions().SignaturePropertyNames);
        Assert.Empty(new ErrorWatchOptions { SignatureProperties = "   " }.SignaturePropertyNames);
    }
}
