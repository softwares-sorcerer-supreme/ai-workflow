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
              "usage": { "inputTokens": 53846, "outputTokens": 82, "cacheReadTokens": 0, "cacheWriteTokens": 53842 },
              "totalNanoAiu": 677165000,
              "tokenDetails": { "input": { "tokenCount": 4 }, "cache_write": { "tokenCount": 53842 } }
            }
          },
          "currentModel": "claude-haiku-5.5"
        }
        """;

    [Fact]
    public void A_real_usage_file_gives_the_credits_and_the_four_token_kinds()
    {
        var usage = CopilotUsage.Parse(RealFile, "copilot")?.Total;

        Assert.Equal(new CopilotUsage(677165000, 4, 0, 53842, 82), usage);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1, 2]")]
    [InlineData("""{ "currentModel": "x" }""")]
    public void A_file_without_cost_or_tokens_gives_nothing(string json)
    {
        Assert.Null(CopilotUsage.Parse(json, "copilot"));
    }

    [Fact]
    public void Missing_token_kinds_count_as_zero()
    {
        var usage = CopilotUsage.Parse("""{ "tokenDetails": { "output": { "tokenCount": 5 } } }""", "copilot")?.Total;

        Assert.Equal(new CopilotUsage(0, 0, 0, 0, 5), usage);
    }

    [Fact]
    public void Agent_metrics_give_one_part_per_agent_and_model()
    {
        const string json = """
            {
              "totalNanoAiu": 30,
              "tokenDetails": { "output": { "tokenCount": 3 } },
              "modelMetrics": { "sonnet": { "totalNanoAiu": 30 } },
              "agentMetrics": {
                "main": {
                  "modelMetrics": {
                    "sonnet": { "totalNanoAiu": 20, "tokenDetails": { "output": { "tokenCount": 2 } } }
                  }
                },
                "code-reviewer": { "modelMetrics": { "haiku": { "totalNanoAiu": 10 } } }
              }
            }
            """;

        var usage = CopilotUsage.Parse(json, "round-1-copilot")!;

        Assert.Equal(new CopilotUsage(30, 0, 0, 0, 3), usage.Total);
        Assert.Equal(
            [
                new UsagePart("round-1-copilot", "main", "sonnet", new CopilotUsage(20, 0, 0, 0, 2)),
                new UsagePart("round-1-copilot", "code-reviewer", "haiku", new CopilotUsage(10, 0, 0, 0, 0)),
            ],
            usage.Parts);
    }

    [Fact]
    public void Without_agent_metrics_the_models_belong_to_the_main_agent()
    {
        var usage = CopilotUsage.Parse(RealFile, "copilot")!;

        var part = Assert.Single(usage.Parts);
        Assert.Equal(("copilot", "main", "claude-haiku-5.5"), (part.Run, part.Agent, part.Model));
    }

    [Fact]
    public void Two_runs_add_up_field_by_field()
    {
        var first = new CopilotUsage(10, 1, 2, 3, 4);
        var second = new CopilotUsage(5, 10, 20, 30, 40);

        Assert.Equal(new CopilotUsage(15, 11, 22, 33, 44), first.Add(second));
    }
}
