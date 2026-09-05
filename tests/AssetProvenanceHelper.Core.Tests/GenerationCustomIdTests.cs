using AssetProvenanceHelper.Core.Generation;

namespace AssetProvenanceHelper.Core.Tests;

public sealed class GenerationCustomIdTests
{
    [Fact]
    public void Create_FormatsExpectedDeterministicPattern()
    {
        var fp = "abcdef0123456789";
        var rk = "11223344556677889900aabbccddeeff";

        var id = GenerationCustomId.Create(fp, rk);

        Assert.Equal("aph-abcdef012345-1122334455667788", id);
    }

    [Fact]
    public void TryParse_ValidCustomId_ExtractsComponents()
    {
        var id = "aph-abcdef012345-1122334455667788";

        var success = GenerationCustomId.TryParse(id, out var fp, out var rk);

        Assert.True(success);
        Assert.Equal("abcdef012345", fp);
        Assert.Equal("1122334455667788", rk);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("custom-123")]
    [InlineData("aph-")]
    [InlineData("aph-xyz-123")]
    public void TryParse_InvalidCustomId_ReturnsFalse(string invalidId)
    {
        var success = GenerationCustomId.TryParse(invalidId, out var fp, out var rk);
        Assert.False(success);
        Assert.Equal(string.Empty, fp);
        Assert.Equal(string.Empty, rk);
    }

    [Fact]
    public void Create_ShortInputs_UsesEntireStrings()
    {
        var id = GenerationCustomId.Create("abc", "123");
        Assert.Equal("aph-abc-123", id);
    }

    [Theory]
    [InlineData(null, "123")]
    [InlineData("", "123")]
    [InlineData("   ", "123")]
    [InlineData("abc", null)]
    [InlineData("abc", "")]
    [InlineData("abc", "   ")]
    public void Create_NullOrWhitespace_ThrowsArgumentException(string? fp, string? rk)
    {
        Assert.ThrowsAny<ArgumentException>(() => GenerationCustomId.Create(fp!, rk!));
    }

    [Fact]
    public void Create_ExactBoundaryLengths_UsesFullStrings()
    {
        var fp12 = "123456789012";
        var rk16 = "1234567890123456";
        var id = GenerationCustomId.Create(fp12, rk16);
        Assert.Equal("aph-123456789012-1234567890123456", id);

        var fp13 = "1234567890123";
        var rk17 = "12345678901234567";
        var idTrunc = GenerationCustomId.Create(fp13, rk17);
        Assert.Equal("aph-123456789012-1234567890123456", idTrunc);
    }
}
