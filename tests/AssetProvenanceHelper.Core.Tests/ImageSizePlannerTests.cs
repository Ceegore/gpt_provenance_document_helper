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
        var ex = Assert.Throws<ArgumentException>(() => ImageSizePlanner.Plan(3200, 1000));
        Assert.Contains("exceeds the maximum allowed ratio", ex.Message);
    }

    [Fact]
    public void Plan_ExceedingMaxEdge_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() => ImageSizePlanner.Plan(4000, 2000));
        Assert.Contains("exceed maximum edge", ex.Message);
    }


    [Fact]
    public void Plan_ExactBoundaryConditions()
    {
        // 3:1 aspect ratio boundary (3000x1000 is valid)
        var plan3To1 = ImageSizePlanner.Plan(3000, 1000);
        Assert.NotNull(plan3To1);

        // Aspect ratio exceeding 3:1 slightly
        Assert.Throws<ArgumentException>(() => ImageSizePlanner.Plan(3001, 1000));

        // Exact MinPixels: 640 x 1024 = 655,360
        var planMin = ImageSizePlanner.Plan(640, 1024);
        Assert.Equal(640, planMin.TargetWidth);
        Assert.Equal(1024, planMin.TargetHeight);
        Assert.Equal(640, planMin.GenerationWidth);
        Assert.Equal(1024, planMin.GenerationHeight);

        // Max pixels exceeded: 3000 x 2800 = 8,400,000 > 8,294,400
        Assert.Throws<ArgumentException>(() => ImageSizePlanner.Plan(3000, 2800));

        // Max edge exceeded
        Assert.Throws<ArgumentException>(() => ImageSizePlanner.Plan(4097, 1000));
        Assert.Throws<ArgumentException>(() => ImageSizePlanner.Plan(1000, 4097));
    }

    [Fact]
    public void Plan_TallerCanvas_CropsHeight()
    {
        // Target is wider than generation canvas aspect -> crop height
        var plan = ImageSizePlanner.Plan(1000, 1200);
        Assert.Equal(1000, plan.TargetWidth);
        Assert.Equal(1200, plan.TargetHeight);
        Assert.True(plan.CropHeight <= plan.GenerationHeight);
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

    [Fact]
    public void Plan_ExceedingMaxPixels_ThrowsArgumentException()
    {
        // 3500x2500: edge <= 3840, aspect 1.4:1 <= 3:1, but 8.80M pixels > 8.29M MaxPixels (3504x2512 = 8,802,048)
        var ex = Assert.Throws<ArgumentException>(() => ImageSizePlanner.Plan(3500, 2500));
        Assert.Contains("exceed maximum allowed", ex.Message);
        Assert.Contains("8802048 pixels", ex.Message);
    }


    [Theory]
    [InlineData(150, 450)]
    [InlineData(450, 150)]
    public void Plan_SmallNonSquareDimensions_IteratesAspectAdjustment(int w, int h)
    {
        var plan = ImageSizePlanner.Plan(w, h);
        Assert.True((long)plan.GenerationWidth * plan.GenerationHeight >= ImageSizePlanner.MinPixels);
        Assert.True(plan.GenerationWidth <= ImageSizePlanner.MaxEdge);
        Assert.True(plan.GenerationHeight <= ImageSizePlanner.MaxEdge);
    }

    [Fact]
    public void Plan_ScaleMultiplication_ScalesUpSmallTarget()
    {
        var plan = ImageSizePlanner.Plan(160, 160);
        Assert.Equal(816, plan.GenerationWidth);
        Assert.Equal(816, plan.GenerationHeight);
        Assert.True(plan.RequiresResize);
    }

    [Fact]
    public void Plan_ExactSquare_NoCropNoResize()
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

    [Fact]
    public void Plan_MaxEdgeAndPixelsExactBoundaries()
    {
        // 3840 x 1280 has edge == 3840 (MaxEdge), aspect 3:1 -> valid
        var planEdge = ImageSizePlanner.Plan(3840, 1280);
        Assert.Equal(3840, planEdge.GenerationWidth);
        Assert.Equal(1280, planEdge.GenerationHeight);

        // 1280 x 3840 has hGen == 3840 (MaxEdge) -> valid, ensures hGen >= MaxEdge mutant is killed
        var planEdgeH = ImageSizePlanner.Plan(1280, 3840);
        Assert.Equal(1280, planEdgeH.GenerationWidth);
        Assert.Equal(3840, planEdgeH.GenerationHeight);

        // 3841 x 1280 exceeds MaxEdge -> throws
        Assert.Throws<ArgumentException>(() => ImageSizePlanner.Plan(3841, 1280));
        Assert.Throws<ArgumentException>(() => ImageSizePlanner.Plan(1280, 3841));

        // 3840 x 2160 = 8,294,400 pixels == MaxPixels -> valid
        var planMaxPixels = ImageSizePlanner.Plan(3840, 2160);
        Assert.Equal(3840, planMaxPixels.GenerationWidth);
        Assert.Equal(2160, planMaxPixels.GenerationHeight);
    }

    [Fact]
    public void Plan_ZeroOrNegativeDimensions_ThrowsArgumentOutOfRangeException()
    {
        var exW0 = Assert.Throws<ArgumentOutOfRangeException>(() => ImageSizePlanner.Plan(0, 1000));
        Assert.Equal("targetWidth", exW0.ParamName);
        Assert.Contains("Target width must be greater than zero", exW0.Message);
        var exWNeg = Assert.Throws<ArgumentOutOfRangeException>(() => ImageSizePlanner.Plan(-10, 1000));
        Assert.Equal("targetWidth", exWNeg.ParamName);

        var exH0 = Assert.Throws<ArgumentOutOfRangeException>(() => ImageSizePlanner.Plan(1000, 0));
        Assert.Equal("targetHeight", exH0.ParamName);
        Assert.Contains("Target height must be greater than zero", exH0.Message);
        var exHNeg = Assert.Throws<ArgumentOutOfRangeException>(() => ImageSizePlanner.Plan(1000, -10));
        Assert.Equal("targetHeight", exHNeg.ParamName);
    }

    [Fact]
    public void Plan_CropOffsets_CalculatedAccurately()
    {
        // 1003 x 3000: wGen=1008, hGen=3008, cropHeight=3008, cropWidth=1006, cropX=(1008-1006)/2 = 1
        var planW = ImageSizePlanner.Plan(1003, 3000);
        Assert.Equal(1, planW.CropX);
        Assert.Equal(0, planW.CropY);

        // 3000 x 1003: wGen=3008, hGen=1008, cropWidth=3008, cropHeight=1006, cropY=(1008-1006)/2 = 1
        var planH = ImageSizePlanner.Plan(3000, 1003);
        Assert.Equal(0, planH.CropX);
        Assert.Equal(1, planH.CropY);
    }
}
