using System.Net;
using AssetProvenanceHelper.Core.Generation.Providers.OpenAi;

namespace AssetProvenanceHelper.Core.Tests;

public sealed class OpenAiErrorParserTests
{
    [Fact]
    public void Parse_NullOrWhiteSpace_ReturnsDefaultMessage()
    {
        var ex1 = OpenAiErrorParser.Parse(HttpStatusCode.BadRequest, null, "req-1");
        Assert.Equal(HttpStatusCode.BadRequest, ex1.StatusCode);
        Assert.Equal("req-1", ex1.RequestId);
        Assert.Contains("failed with HTTP 400", ex1.Message);

        var ex2 = OpenAiErrorParser.Parse(HttpStatusCode.InternalServerError, "   ", null);
        Assert.Equal(HttpStatusCode.InternalServerError, ex2.StatusCode);
        Assert.Contains("failed with HTTP 500", ex2.Message);
    }

    [Fact]
    public void Parse_StandardJsonError_ExtractsDetails()
    {
        var json = "{\"error\":{\"message\":\"Invalid model\",\"type\":\"invalid_request_error\",\"code\":\"model_not_found\"}}";
        var ex = OpenAiErrorParser.Parse(HttpStatusCode.NotFound, json, "req-2");

        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
        Assert.Equal("model_not_found", ex.ErrorCode);
        Assert.Equal("invalid_request_error", ex.ErrorType);
        Assert.Equal("Invalid model", ex.Message);
        Assert.Equal("req-2", ex.RequestId);
    }

    [Fact]
    public void Parse_JsonErrorWithoutMessage_UsesTypeAndCode()
    {
        var json = "{\"error\":{\"message\":\"\",\"type\":\"quota_exceeded\",\"code\":\"insufficient_quota\"}}";
        var ex = OpenAiErrorParser.Parse(HttpStatusCode.TooManyRequests, json, "req-3");

        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
        Assert.Equal("insufficient_quota", ex.ErrorCode);
        Assert.Equal("quota_exceeded", ex.ErrorType);
        Assert.Contains("quota_exceeded", ex.Message);
    }

    [Fact]
    public void Parse_HtmlWithTitle_ExtractsTitle()
    {
        var html = "<html><head><title>Bad Gateway</title></head><body>502</body></html>";
        var ex = OpenAiErrorParser.Parse(HttpStatusCode.BadGateway, html, "req-4");

        Assert.Equal(HttpStatusCode.BadGateway, ex.StatusCode);
        Assert.Equal("html_gateway_error", ex.ErrorType);
        Assert.Contains("Bad Gateway", ex.Message);
    }

    [Fact]
    public void Parse_HtmlWithoutTitle_FallsBackToTrimmedBody()
    {
        var html = "<html><body>Unknown error without title tag</body></html>";
        var ex = OpenAiErrorParser.Parse(HttpStatusCode.ServiceUnavailable, html, "req-5");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
        Assert.Contains("Unknown error without title tag", ex.Message);
    }

    [Fact]
    public void Parse_NonJsonLongBody_TrimsTo300Chars()
    {
        var longBody = new string('A', 400);
        var ex = OpenAiErrorParser.Parse(HttpStatusCode.InternalServerError, longBody, "req-6");

        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
        Assert.Contains("...", ex.Message);
    }
}
