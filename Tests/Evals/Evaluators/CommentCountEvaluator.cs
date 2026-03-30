using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;

namespace CodeReviewAgent.Tests.Evals.Evaluators;

/// <summary>
/// Custom evaluator that counts the number of review comments returned by the agent.
/// Does NOT require an LLM — it simply parses the JSON array in the response.
///
/// Use this to verify clean code produces minimal comments (anti-hallucination check)
/// and that vulnerable code produces a meaningful number of comments.
/// </summary>
public sealed class CommentCountEvaluator : IEvaluator
{
    public const string MetricName = "comment_count";

    /// <summary>Expected maximum comment count (inclusive). Default: int.MaxValue.</summary>
    public int MaxExpected { get; init; } = int.MaxValue;

    /// <summary>Expected minimum comment count (inclusive). Default: 0.</summary>
    public int MinExpected { get; init; } = 0;

    public IReadOnlyCollection<string> EvaluationMetricNames => [MetricName];

    public ValueTask<EvaluationResult> EvaluateAsync(
        IEnumerable<ChatMessage> messages,
        ChatResponse modelResponse,
        ChatConfiguration? chatConfiguration = null,
        IEnumerable<EvaluationContext>? additionalContext = null,
        CancellationToken cancellationToken = default)
    {
        var responseText = modelResponse.Text ?? string.Empty;
        int count = CountComments(responseText);

        bool failed = count < MinExpected || count > MaxExpected;
        var rating = failed ? EvaluationRating.Poor : EvaluationRating.Good;
        var reason = failed
            ? $"Expected between {MinExpected} and {MaxExpected} comments, but got {count}."
            : $"Comment count {count} is within expected range [{MinExpected}, {MaxExpected}].";

        var metric = new NumericMetric(MetricName, count)
        {
            Interpretation = new EvaluationMetricInterpretation(rating, failed, reason)
        };

        return ValueTask.FromResult(new EvaluationResult(metric));
    }

    private static int CountComments(string responseText)
    {
        try
        {
            var start = responseText.IndexOf('[');
            var end = responseText.LastIndexOf(']');
            if (start < 0 || end <= start) return 0;

            using var doc = JsonDocument.Parse(responseText[start..(end + 1)]);
            return doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.GetArrayLength()
                : 0;
        }
        catch
        {
            return 0;
        }
    }
}
