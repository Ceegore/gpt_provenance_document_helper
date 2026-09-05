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
        var body300 = new string('A', 300);
        var ex300 = OpenAiErrorParser.Parse(HttpStatusCode.InternalServerError, body300, "req-6a");
        Assert.Contains(body300, ex300.Message);
        Assert.DoesNotContain("...", ex300.Message);

        var body301 = new string('A', 301);
        var ex301 = OpenAiErrorParser.Parse(HttpStatusCode.InternalServerError, body301, "req-6b");
        Assert.Contains("...", ex301.Message);
    }

    [Fact]
    public void Parse_CaseInsensitiveKeys_ParsesSuccessfully()
    {
        var json = "{\"ERROR\":{\"MESSAGE\":\"Case insensitive message\",\"TYPE\":\"case_type\",\"CODE\":\"case_code\"}}";
        var ex = OpenAiErrorParser.Parse(HttpStatusCode.BadRequest, json, "req-case");

        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
        Assert.Equal("case_code", ex.ErrorCode);
        Assert.Equal("case_type", ex.ErrorType);
        Assert.Equal("Case insensitive message", ex.Message);
        Assert.Equal("req-case", ex.RequestId);
    }
}
