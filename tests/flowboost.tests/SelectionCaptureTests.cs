using flowboost.Services;
using Xunit;

namespace flowboost.Tests;

public sealed class SelectionCaptureTests
{
    [Fact]
    public void ChangedClipboardWithTextIsSuccess()
    {
        var result = SelectionService.ResolveClipboardObservation(false, true, "selected text");

        Assert.Equal(SelectionCaptureStatus.Success, result.Status);
        Assert.Equal("selected text", result.Text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    public void ChangedClipboardWithoutUsableTextIsNoText(string? text)
    {
        var result = SelectionService.ResolveClipboardObservation(false, true, text);

        Assert.Equal(SelectionCaptureStatus.NoText, result.Status);
        Assert.Null(result.Text);
    }

    [Fact]
    public void UnchangedClipboardIsNotReportedAsNoSelection()
    {
        var result = SelectionService.ResolveClipboardObservation(false, false, null);

        Assert.Equal(SelectionCaptureStatus.ClipboardUnchanged, result.Status);
    }

    [Fact]
    public void ForegroundChangeTakesPrecedenceOverClipboardText()
    {
        var result = SelectionService.ResolveClipboardObservation(true, true, "selected text");

        Assert.Equal(SelectionCaptureStatus.ForegroundChanged, result.Status);
        Assert.Null(result.Text);
    }
}
