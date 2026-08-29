#nullable enable
namespace AssetProvenanceHelper.Tests;

public class UpgradeV13PromptPreviewTests
{
    [Fact]
    public void EmptyPromptShowsNoPromptStored()
    {
        Assert.Equal(
            "No prompt stored.",
            MainForm.BuildPromptPreview(null));

        Assert.Equal(
            "No prompt stored.",
            MainForm.BuildPromptPreview(string.Empty));
    }

    [Fact]
    public void OneCharShownExactly()
    {
        Assert.Equal(
            "a",
            MainForm.BuildPromptPreview("a"));
    }

    [Fact]
    public void NinetyNineCharsShownExactly()
    {
        var prompt =
            new string('x', 99);

        Assert.Equal(prompt, MainForm.BuildPromptPreview(prompt));
    }

    [Fact]
    public void OneHundredCharsNoEllipsis()
    {
        var prompt =
            new string('x', 100);

        var preview =
            MainForm.BuildPromptPreview(prompt);

        Assert.Equal(100, preview.Length);
        Assert.DoesNotContain("...", preview);
    }

    [Fact]
    public void OneHundredOneCharsTruncatedWithEllipsis()
    {
        var prompt =
            new string('x', 101);

        var preview =
            MainForm.BuildPromptPreview(prompt);

        Assert.Equal(103, preview.Length);
        Assert.Equal(
            new string('x', 100) + "...",
            preview);
    }

    [Fact]
    public void OneThousandCharsTruncated()
    {
        var prompt =
            new string('y', 1000);

        var preview =
            MainForm.BuildPromptPreview(prompt);

        Assert.Equal(103, preview.Length);
    }

    [Fact]
    public void CrLfBecomeSpaces()
    {
        var preview =
            MainForm.BuildPromptPreview(
                "line1\r\nline2");

        Assert.Equal("line1  line2", preview);
    }

    [Fact]
    public void TabsBecomeSpaces()
    {
        var preview =
            MainForm.BuildPromptPreview(
                "a\tb");

        Assert.Equal("a b", preview);
    }

    [Fact]
    public void UnicodePreserved()
    {
        var prompt =
            "Äpfel 🍎 ünïcode 测试";

        var preview =
            MainForm.BuildPromptPreview(prompt);

        Assert.Equal(prompt, preview);
    }
}