using CodeReviewAgent.Tests.Evals.EvalFixtures;
using CodeReviewAgent.Tests.Evals.Evaluators;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Quality;
using Xunit;

namespace CodeReviewAgent.Tests.Evals;

/// <summary>
/// Eval tests that measure the quality of the code review agent's output using
/// Microsoft.Extensions.AI.Evaluation evaluators.
///
/// These tests call the REAL Azure OpenAI model and are intentionally separated
/// from fast unit tests. Run them with:
///
///   dotnet test --filter "Category=Eval"
///
/// Prerequisites: AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_API_KEY, and
/// AZURE_OPENAI_DEPLOYMENT must be set (via .env or environment variables).
/// </summary>
[Trait("Category", "Eval")]
public class CodeReviewQualityEvalTests : IAsyncLifetime
{
    // Shared across all tests — avoids redundant LLM calls on setup
    private ChatConfiguration _evalChatConfig = null!;
    private ChatResponse _securityCodeResponse = null!;
    private ChatResponse _cleanCodeResponse = null!;
    private IList<ChatMessage> _securityCodeMessages = null!;
    private IList<ChatMessage> _cleanCodeMessages = null!;

    // Evaluator instances (metric names are instance properties)
    private readonly CoherenceEvaluator _coherenceEvaluator = new();
    private readonly RelevanceEvaluator _relevanceEvaluator = new();
    private readonly GroundednessEvaluator _groundednessEvaluator = new();
    private readonly CommentCountEvaluator _commentCountEvaluator = new();
    private readonly SeverityAccuracyEvaluator _severityEvaluator = new();

    // -------------------------------------------------------------------------
    // Setup / Teardown
    // -------------------------------------------------------------------------

    public async Task InitializeAsync()
    {
        _evalChatConfig = EvalConfiguration.CreateEvaluatorChatConfiguration();

        using var agentClient = EvalConfiguration.CreateChatClient();
        var chatOptions = new ChatOptions { Temperature = 0.0f };

        // Run both fixtures once and cache the responses.
        // Temperature = 0 maximises reproducibility across runs.
        (_securityCodeMessages, _securityCodeResponse) =
            await RunAgentReviewAsync(agentClient, GoldenCodeSamples.SecurityVulnerableCSharp, chatOptions);

        (_cleanCodeMessages, _cleanCodeResponse) =
            await RunAgentReviewAsync(agentClient, GoldenCodeSamples.CleanCSharp, chatOptions);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // -------------------------------------------------------------------------
    // Quality evaluator tests (LLM-as-judge)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SecurityCode_ReviewComments_AreCoherent()
    {
        var result = await _coherenceEvaluator.EvaluateAsync(
            _securityCodeMessages,
            _securityCodeResponse,
            _evalChatConfig);

        var metric = result.Get<NumericMetric>(CoherenceEvaluator.CoherenceMetricName);

        metric.Interpretation.Should().NotBeNull();
        metric.Interpretation!.Failed.Should().BeFalse(
            $"Review comments should be coherent. Score={metric.Value}, Reason={metric.Interpretation.Reason}");
        metric.Interpretation.Rating.Should().BeOneOf(
            [EvaluationRating.Good, EvaluationRating.Exceptional],
            $"Expected Good or Exceptional coherence, got {metric.Interpretation.Rating}. Score={metric.Value}");
    }

    [Fact]
    public async Task SecurityCode_ReviewComments_AreRelevant()
    {
        var result = await _relevanceEvaluator.EvaluateAsync(
            _securityCodeMessages,
            _securityCodeResponse,
            _evalChatConfig);

        var metric = result.Get<NumericMetric>(RelevanceEvaluator.RelevanceMetricName);

        metric.Interpretation.Should().NotBeNull();
        metric.Interpretation!.Failed.Should().BeFalse(
            $"Review comments should be relevant to the code diff. Score={metric.Value}, Reason={metric.Interpretation.Reason}");
        metric.Interpretation.Rating.Should().BeOneOf(
            [EvaluationRating.Good, EvaluationRating.Exceptional],
            $"Expected Good or Exceptional relevance. Score={metric.Value}");
    }

    [Fact]
    public async Task SecurityCode_ReviewComments_AreGrounded()
    {
        // GroundednessEvaluatorContext tells the judge what the code diff actually says,
        // so it can verify the agent didn't hallucinate issues.
        var groundingContext = new GroundednessEvaluatorContext(
            GoldenCodeSamples.SecurityVulnerableCSharp.UnifiedDiff);

        var result = await _groundednessEvaluator.EvaluateAsync(
            _securityCodeMessages,
            _securityCodeResponse,
            _evalChatConfig,
            additionalContext: [groundingContext]);

        var metric = result.Get<NumericMetric>(GroundednessEvaluator.GroundednessMetricName);

        metric.Interpretation.Should().NotBeNull();
        metric.Interpretation!.Failed.Should().BeFalse(
            $"Review comments should reference actual code. Score={metric.Value}, Reason={metric.Interpretation.Reason}");
        metric.Interpretation.Rating.Should().BeOneOf(
            [EvaluationRating.Good, EvaluationRating.Exceptional],
            $"Expected Good or Exceptional groundedness. Score={metric.Value}");
    }

    // -------------------------------------------------------------------------
    // Custom evaluator tests (no LLM required)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SecurityCode_ProducesAtLeastOneHighSeverityComment()
    {
        // The fixture has hardcoded credentials + SQL injection — the agent must
        // flag at least one issue as HIGH severity.
        var result = await _severityEvaluator.EvaluateAsync(
            _securityCodeMessages,
            _securityCodeResponse);

        var metric = result.Get<NumericMetric>(SeverityAccuracyEvaluator.MetricName);

        metric.Interpretation.Should().NotBeNull();
        metric.Interpretation!.Failed.Should().BeFalse(metric.Interpretation.Reason);
        metric.Value.Should().Be(1.0,
            "Agent must flag at least one high-severity security issue in vulnerable code.");
    }

    [Fact]
    public async Task CleanCode_DoesNotOverFlag()
    {
        // Clean code with no intentional issues — agent should return 0-3 comments.
        // Catches hallucination or over-aggressive flagging.
        var evaluator = new CommentCountEvaluator { MinExpected = 0, MaxExpected = 3 };
        var result = await evaluator.EvaluateAsync(
            _cleanCodeMessages,
            _cleanCodeResponse);

        var metric = result.Get<NumericMetric>(CommentCountEvaluator.MetricName);

        metric.Interpretation.Should().NotBeNull();
        metric.Interpretation!.Failed.Should().BeFalse(metric.Interpretation.Reason);
    }

    [Fact]
    public async Task SecurityCode_ProducesSubstantialComments()
    {
        // The fixture has 3 distinct security issues — agent should produce >= 2 comments.
        var evaluator = new CommentCountEvaluator { MinExpected = 2, MaxExpected = 20 };
        var result = await evaluator.EvaluateAsync(
            _securityCodeMessages,
            _securityCodeResponse);

        var metric = result.Get<NumericMetric>(CommentCountEvaluator.MetricName);

        metric.Interpretation.Should().NotBeNull();
        metric.Interpretation!.Failed.Should().BeFalse(metric.Interpretation.Reason);
    }

    // -------------------------------------------------------------------------
    // Helper
    // -------------------------------------------------------------------------

    /// <summary>
    /// Builds a representative code review prompt and calls the LLM directly,
    /// returning the conversation messages and the real ChatResponse for evaluation.
    /// </summary>
    private static async Task<(IList<ChatMessage> messages, ChatResponse response)> RunAgentReviewAsync(
        IChatClient chatClient,
        Models.PullRequestFile file,
        ChatOptions options)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, """
                You are an expert C#/.NET code reviewer with deep knowledge of C# best practices,
                .NET patterns, SOLID principles, security vulnerabilities (OWASP Top 10), performance
                optimisation, and async/await. Review ONLY the lines prefixed with '+' in the diff.

                Return your findings as a JSON array. Each item must have:
                  "lineNumber": (int) the line number in the diff,
                  "severity": "high" | "medium" | "low",
                  "type": "issue" | "suggestion" | "nitpick",
                  "comment": (string) a clear explanation and specific recommendation.

                If no issues are found, return an empty array: []
                """),

            new(ChatRole.User, $"""
                Review the following C#/.NET code change.

                File: {file.Path}
                Change type: {file.ChangeType}

                === DIFF (lines with '+' are new/modified — only review these) ===
                ```diff
                {file.UnifiedDiff}
                ```

                === FULL FILE CONTENT (context only — do not flag lines here) ===
                ```csharp
                {file.Content}
                ```
                """)
        };

        var response = await chatClient.GetResponseAsync(messages, options);
        return (messages, response);
    }
}
