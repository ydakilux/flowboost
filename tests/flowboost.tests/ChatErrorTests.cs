using flowboost.Services;
using Xunit;

namespace flowboost.Tests;

public sealed class ChatErrorTests
{
    [Fact]
    public void ClassifiesServiceUnavailableAndIncludesModelWithoutRequestId()
    {
        const string raw = "Execution failed: Failed to get response from the AI model; retried 5 times (Request-ID ABC:123). Last error: 503 Grok 4.5 is currently experiencing issues.";

        var error = ChatSession.ClassifyError(raw, "Grok 4.5");

        Assert.Equal(ChatErrorKind.ModelUnavailable, error.Kind);
        Assert.Contains("Grok 4.5", error.Message);
        Assert.DoesNotContain("Request-ID", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ClassifiesExpiredGitHubSignIn()
    {
        var error = ChatSession.ClassifyError("No GitHub OAuth token is available.", "gpt-5");

        Assert.Equal(ChatErrorKind.SignInExpired, error.Kind);
        Assert.Equal("Your GitHub sign-in has expired. Sign in again from Settings.", error.Message);
    }

    [Fact]
    public void RemovesRequestIdFromOtherErrors()
    {
        var error = ChatSession.ClassifyError("  Something went wrong (Request-ID ABC:123) please retry.  ", "gpt-5");

        Assert.Equal(ChatErrorKind.Other, error.Kind);
        Assert.Equal("Something went wrong please retry.", error.Message);
    }

    [Fact]
    public void CapsOtherErrorMessagesAt400Characters()
    {
        var error = ChatSession.ClassifyError(new string('x', 450), "gpt-5");

        Assert.Equal(ChatErrorKind.Other, error.Kind);
        Assert.Equal(400, error.Message.Length);
    }
}
