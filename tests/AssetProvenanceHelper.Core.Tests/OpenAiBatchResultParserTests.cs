using AssetProvenanceHelper.Core.Generation.Providers.OpenAi;

namespace AssetProvenanceHelper.Core.Tests;

public sealed class OpenAiBatchResultParserTests
{
    [Fact]
    public void ParseResults_SuccessAndErrorLines_MappedByCustomId()
    {
        var rawBase64 = Convert.ToBase64String(new byte[] { 1, 2, 3, 4 });
        var outputJsonl =
            "{\"id\":\"batch_req_1\",\"custom_id\":\"aph-custom-1\",\"response\":{\"status_code\":200,\"request_id\":\"req-1\",\"body\":{\"data\":[{\"b64_json\":\"" + rawBase64 + "\"}]}},\"error\":null}\n" +
            "{\"id\":\"batch_req_2\",\"custom_id\":\"aph-custom-2\",\"response\":{\"status_code\":200,\"request_id\":\"req-2\",\"body\":{\"data\":[{\"b64_json\":\"" + rawBase64 + "\"}]}},\"error\":null}";

        var errorJsonl =
            "{\"id\":\"batch_req_3\",\"custom_id\":\"aph-custom-3\",\"response\":{\"status_code\":400,\"request_id\":\"req-3\"},\"error\":{\"message\":\"Safety policy violation\",\"type\":\"invalid_request_error\",\"code\":\"content_policy_violation\"}}";

        var results = OpenAiBatchResultParser.ParseResults(outputJsonl, errorJsonl);

        Assert.Equal(3, results.Count);

        var item1 = results.First(r => r.CustomId == "aph-custom-1");
        Assert.True(item1.IsSuccess);
        Assert.Equal(200, item1.StatusCode);
        Assert.NotNull(item1.ImageBytes);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, item1.ImageBytes);

        var item3 = results.First(r => r.CustomId == "aph-custom-3");
        Assert.False(item3.IsSuccess);
        Assert.Equal("content_policy_violation", item3.ErrorCode);
        Assert.Contains("Safety policy violation", item3.ErrorMessage);
    }

    [Fact]
    public void ParseResults_OutOfOrderLines_PreservesMapping()
    {
        var rawBase64 = Convert.ToBase64String(new byte[] { 42 });
        var outputJsonl =
            "{\"id\":\"req_b\",\"custom_id\":\"aph-item-b\",\"response\":{\"status_code\":200,\"body\":{\"data\":[{\"b64_json\":\"" + rawBase64 + "\"}]}}}\n" +
            "{\"id\":\"req_a\",\"custom_id\":\"aph-item-a\",\"response\":{\"status_code\":200,\"body\":{\"data\":[{\"b64_json\":\"" + rawBase64 + "\"}]}}}";

        var results = OpenAiBatchResultParser.ParseResults(outputJsonl, null);

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.CustomId == "aph-item-a");
        Assert.Contains(results, r => r.CustomId == "aph-item-b");
    }

    [Fact]
    public void ParseResults_DuplicateWithinOutput_Throws()
    {
        var rawBase64 = Convert.ToBase64String(new byte[] { 1, 2, 3 });
        var outputJsonl =
            "{\"id\":\"req_1\",\"custom_id\":\"aph-dup-1\",\"response\":{\"status_code\":200,\"body\":{\"data\":[{\"b64_json\":\"" + rawBase64 + "\"}]}}}\n" +
            "{\"id\":\"req_2\",\"custom_id\":\"aph-dup-1\",\"response\":{\"status_code\":200,\"body\":{\"data\":[{\"b64_json\":\"" + rawBase64 + "\"}]}}}";

        var ex = Assert.Throws<InvalidDataException>(() =>
            OpenAiBatchResultParser.ParseResults(outputJsonl, null));

        Assert.Contains("duplicate custom_id", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseResults_CaseChangedId_IsDistinctOrdinal()
    {
        var rawBase64 = Convert.ToBase64String(new byte[] { 1, 2, 3 });
        var outputJsonl =
            "{\"id\":\"req_1\",\"custom_id\":\"aph-item-a\",\"response\":{\"status_code\":200,\"body\":{\"data\":[{\"b64_json\":\"" + rawBase64 + "\"}]}}}\n" +
            "{\"id\":\"req_2\",\"custom_id\":\"APH-ITEM-A\",\"response\":{\"status_code\":200,\"body\":{\"data\":[{\"b64_json\":\"" + rawBase64 + "\"}]}}}";

        var results = OpenAiBatchResultParser.ParseResults(outputJsonl, null);
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void ParseResults_MalformedBase64_ReturnsMalformedBase64Error()
    {
        var outputJsonl =
            "{\"id\":\"req_1\",\"custom_id\":\"aph-bad-b64\",\"response\":{\"status_code\":200,\"body\":{\"data\":[{\"b64_json\":\"not_valid_base64!!!\"}]}}}";

        var results = OpenAiBatchResultParser.ParseResults(outputJsonl, null);

        Assert.Single(results);
        var item = results[0];
        Assert.False(item.IsSuccess);
        Assert.Equal("malformed_base64", item.ErrorCode);
        Assert.Contains("malformed base64", item.ErrorMessage);
    }

    [Fact]
    public void ParseResults_InvalidJsonAndMissingCustomId_SkipsGracefully()
    {
        var outputJsonl =
            "not valid json at all\n" +
            "{\"id\":\"req_empty_custom_id\",\"custom_id\":\"   \"}\n" +
            "{\"id\":\"req_no_custom_id\"}\n";

        var results = OpenAiBatchResultParser.ParseResults(outputJsonl, null);
        Assert.Empty(results);
    }

    [Fact]
    public void ParseResults_200WithEmptyDataOrNoStatusCode_FailsGracefully()
    {
        var outputJsonl =
            "{\"id\":\"req_empty_data\",\"custom_id\":\"aph-c1\",\"response\":{\"status_code\":200,\"body\":{\"data\":[]}}}\n" +
            "{\"id\":\"req_no_status\",\"custom_id\":\"aph-c2\",\"response\":{}}";

        var results = OpenAiBatchResultParser.ParseResults(outputJsonl, null);
        Assert.Equal(2, results.Count);

        var c1 = results.First(r => r.CustomId == "aph-c1");
        Assert.False(c1.IsSuccess);
        Assert.Equal("unknown_error", c1.ErrorCode);

        var c2 = results.First(r => r.CustomId == "aph-c2");
        Assert.False(c2.IsSuccess);
        Assert.Equal(500, c2.StatusCode);
    }

    [Fact]
    public void ParseResults_DuplicateAcrossOutputAndError_Throws()
    {
        var outputJsonl = "{\"id\":\"req_1\",\"custom_id\":\"aph-dup\",\"response\":{\"status_code\":200,\"body\":{\"data\":[{\"b64_json\":\"AQID\"}]}}}";
        var errorJsonl = "{\"id\":\"req_2\",\"custom_id\":\"aph-dup\",\"response\":{\"status_code\":400}}";

        var ex = Assert.Throws<InvalidDataException>(() =>
            OpenAiBatchResultParser.ParseResults(outputJsonl, errorJsonl));

        Assert.Contains("duplicate custom_id", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseResults_CaseInsensitivePropertyNames_ParsesSuccessfully()
    {
        var rawBase64 = Convert.ToBase64String(new byte[] { 9, 9 });
        var upperJsonl = "{\"ID\":\"req_upper\",\"CUSTOM_ID\":\"aph-item-upper\",\"RESPONSE\":{\"STATUS_CODE\":200,\"BODY\":{\"DATA\":[{\"B64_JSON\":\"" + rawBase64 + "\"}]}}}\n";
        var results = OpenAiBatchResultParser.ParseResults(upperJsonl, null);
        Assert.Single(results);
        Assert.Equal("aph-item-upper", results[0].CustomId);
        Assert.True(results[0].IsSuccess);
    }
}
