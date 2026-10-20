using AiRunner.Core;

namespace AiRunner.Tests.Core;

/// <summary>The JSON of copilot --usage-output-file: the totals the cost row keeps.</summary>
public sealed class CopilotUsageTests
{
    /// <summary>A real file from "copilot -p" with one model, shortened to the parts the runner reads.</summary>
    private const string RealFile = """
        {
          "totalPremiumRequestCost": 1,
          "totalUserRequests": 1,
          "totalNanoAiu": 677165000,
          "tokenDetails": {
            "input": { "tokenCount": 4 },
            "cache_read": { "tokenCount": 0 },
            "cache_write": { "tokenCount": 53842 },
            "output": { "tokenCount": 82 }
          },
          "modelMetrics": {
            "claude-haiku-5.5": {
              "requests": { "count": 1, "cost": 1 },
              "usage": { "inputTokens": 53846, "outputTokens": 82, "cacheReadTokens": 0, "cacheWriteTokens": 53842 }
            }
          },
          "currentModel": "claude-haiku-5.5"
        }
        """;

    [Fact]
    public void A_real_usage_file_gives_the_credits_and_the_four_token_kinds()
    {
        var usage = CopilotUsage.Parse(RealFile);

        Assert.Equal(new CopilotUsage(677165000, 4, 0, 53842, 82), usage);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1, 2]")]
    [InlineData("""{ "currentModel": "x" }""")]
    public void A_file_without_cost_or_tokens_gives_nothing(string json)
    {
        Assert.Null(CopilotUsage.Parse(json));
    }

    [Fact]
    public void Missing_token_kinds_count_as_zero()
    {
        var usage = CopilotUsage.Parse("""{ "tokenDetails": { "output": { "tokenCount": 5 } } }""");

        Assert.Equal(new CopilotUsage(0, 0, 0, 0, 5), usage);
    }

    [Fact]
    public void Two_runs_add_up_field_by_field()
    {
        var first = new CopilotUsage(10, 1, 2, 3, 4);
        var second = new CopilotUsage(5, 10, 20, 30, 40);

        Assert.Equal(new CopilotUsage(15, 11, 22, 33, 44), first.Add(second));
    }
}
