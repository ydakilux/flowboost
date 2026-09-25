using System.Reflection;
using System.IO;
using flowboost.Services;
using Xunit;

namespace flowboost.Tests;

public sealed class CopilotServiceTests
{
    [Theory]
    [InlineData("Not authenticated. Please authenticate first.", true)]
    [InlineData("RPC error: Not authenticated. Please authenticate first.", true)]
    [InlineData("Network request failed.", false)]
    [InlineData("not authenticated", false)]
    public void AuthenticationRejectionClassificationIsNarrow(string message, bool expected)
    {
        var exception = new RemoteRpcException(message);
        var classifier = typeof(CopilotService).GetMethod("IsAuthenticationRejection", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(classifier);
        Assert.Equal(expected, classifier.Invoke(null, [exception]));
    }

    [Fact]
    public void ConnectionLossClassificationDoesNotMatchGenericNetworkFailures()
    {
        var classifier = typeof(CopilotService).GetMethod("IsConnectionLost", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(classifier);
        Assert.Equal(true, classifier.Invoke(null, [new ConnectionLostException()]));
        Assert.Equal(false, classifier.Invoke(null, [new IOException("Network request failed.")]));
    }

    private sealed class RemoteRpcException(string message) : Exception(message);
    private sealed class ConnectionLostException : Exception;
}
