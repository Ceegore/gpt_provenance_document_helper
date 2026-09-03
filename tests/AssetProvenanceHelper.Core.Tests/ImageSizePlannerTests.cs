using AssetProvenanceHelper.Core.Generation;

namespace AssetProvenanceHelper.Core.Tests;

public sealed class ImageSizePlannerTests
{
    [Fact]
    public void Plan_512x512_ProducesValidProviderSizeAndCrop()
    {
        var plan = ImageSizePlanner.Plan(512, 512);

        // Min pixels: 655,360; 816*816 = 665,856; 816 % 16 == 0
        Assert.Equal(816, plan.GenerationWidth);
        Assert.Equal(816, plan.GenerationHeight);
        Assert.Equal(0, plan.GenerationWidth % 16);
        Assert.Equal(0, plan.GenerationHeight % 16);
        Assert.True((long)plan.GenerationWidth * plan.GenerationHeight >= ImageSizePlanner.MinPixels);

        Assert.Equal(512, plan.TargetWidth);
        Assert.Equal(512, plan.TargetHeight);
        Assert.Equal(0, plan.CropX);
        Assert.Equal(0, plan.CropY);
        Assert.Equal(816, plan.CropWidth);
        Assert.Equal(816, plan.CropHeight);
        Assert.True(plan.RequiresResize);
    }

    [Fact]
    public void Plan_1920x1080_ProducesValidProviderSizeAndCenterCrop()
    {
        var plan = ImageSizePlanner.Plan(1920, 1080);

        Assert.Equal(1920, plan.GenerationWidth);
        Assert.Equal(1088, plan.GenerationHeight);
        Assert.Equal(0, plan.GenerationWidth % 16);
        Assert.Equal(0, plan.GenerationHeight % 16);
        Assert.True((long)plan.GenerationWidth * plan.GenerationHeight >= ImageSizePlanner.MinPixels);

        Assert.Equal(1920, plan.TargetWidth);
        Assert.Equal(1080, plan.TargetHeight);
        Assert.Equal(0, plan.CropX);
        Assert.Equal(4, plan.CropY);
        Assert.Equal(1920, plan.CropWidth);
        Assert.Equal(1080, plan.CropHeight);
        Assert.False(plan.RequiresResize);
    }

    [Fact]
    public void Plan_1024x1024_ProducesExactProviderSizeWithoutCropOrResize()
    {
        var plan = ImageSizePlanner.Plan(1024, 1024);

        Assert.Equal(1024, plan.GenerationWidth);
        Assert.Equal(1024, plan.GenerationHeight);
        Assert.Equal(0, plan.CropX);
        Assert.Equal(0, plan.CropY);
        Assert.Equal(1024, plan.CropWidth);
        Assert.Equal(1024, plan.CropHeight);
        Assert.False(plan.RequiresResize);
    }

    [Theory]
    [InlineData(0, 500)]
    [InlineData(500, 0)]
    [InlineData(-100, 100)]
    public void Plan_InvalidDimensions_ThrowsArgumentOutOfRangeException(int w, int h)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageSizePlanner.Plan(w, h));
    }

    [Fact]
    public void Plan_RatioExceeding3To1_ThrowsArgumentException()
    {
        // 3200x1000 is 3.2:1 > 3:1
        Assert.Throws<ArgumentException>(() => ImageSizePlanner.Plan(3200, 1000));
    }

    [Fact]
    public void Plan_ExceedingMaxEdge_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => ImageSizePlanner.Plan(4000, 2000));
    }

    [Theory]
    [InlineData(800, 600)]
    [InlineData(600, 800)]
    [InlineData(1000, 500)]
    [InlineData(500, 1000)]
    [InlineData(2560, 1440)]
    [InlineData(1440, 2560)]
    [InlineData(100, 100)]
    [InlineData(3000, 1000)]
    [InlineData(1000, 3000)]
    public void Plan_AlwaysProducesCropWithinGenerationBounds(int w, int h)
    {
        var plan = ImageSizePlanner.Plan(w, h);
        Assert.True(plan.CropX >= 0);
        Assert.True(plan.CropY >= 0);
        Assert.True(plan.CropWidth > 0);
        Assert.True(plan.CropHeight > 0);
        Assert.True(plan.CropX + plan.CropWidth <= plan.GenerationWidth);
        Assert.True(plan.CropY + plan.CropHeight <= plan.GenerationHeight);
    }
}
