using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;

namespace CodeReviewAgent.Tests.Evals.Evaluators;

/// <summary>
/// Custom evaluator that checks whether the agent correctly classified at least one
/// security issue as HIGH severity when reviewing code with known vulnerabilities.
///
/// Returns a NumericMetric:
///   1.0 = at least one "high" severity comment found  (pass)
///   0.0 = no "high" severity comment found            (fail)
///
/// Does NOT require an LLM — parses the JSON response directly.
/// </summary>
public sealed class SeverityAccuracyEvaluator : IEvaluator
{
    public const string MetricName = "high_severity_found";

    public IReadOnlyCollection<string> EvaluationMetricNames => [MetricName];

    public ValueTask<EvaluationResult> EvaluateAsync(
        IEnumerable<ChatMessage> messages,
        ChatResponse modelResponse,
        ChatConfiguration? chatConfiguration = null,
        IEnumerable<EvaluationContext>? additionalContext = null,
        CancellationToken cancellationToken = default)
    {
        var responseText = modelResponse.Text ?? string.Empty;
        bool hasHighSeverity = ContainsHighSeverityComment(responseText);

        double score = hasHighSeverity ? 1.0 : 0.0;
        bool failed = !hasHighSeverity;
        var rating = hasHighSeverity ? EvaluationRating.Exceptional : EvaluationRating.Poor;
        var reason = hasHighSeverity
            ? "Agent correctly identified at least one HIGH-severity security issue."
            : "Agent did not flag any issues as HIGH severity — expected at least one for known vulnerabilities.";

        var metric = new NumericMetric(MetricName, score)
        {
            Interpretation = new EvaluationMetricInterpretation(rating, failed, reason)
        };

        return ValueTask.FromResult(new EvaluationResult(metric));
    }

    private static bool ContainsHighSeverityComment(string responseText)
    {
        try
        {
            var start = responseText.IndexOf('[');
            var end = responseText.LastIndexOf(']');
            if (start < 0 || end <= start) return false;

            using var doc = JsonDocument.Parse(responseText[start..(end + 1)]);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return false;

            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (element.TryGetProperty("severity", out var sev) &&
                    sev.GetString()?.Equals("high", StringComparison.OrdinalIgnoreCase) == true)
                {
                    return true;
                }
            }
        }
        catch
        {
            // Fall through to text-based fallback
        }

        // Fallback: plain text check for models that don't return strict JSON
        return responseText.Contains("\"high\"", StringComparison.OrdinalIgnoreCase) &&
               responseText.Contains("severity", StringComparison.OrdinalIgnoreCase);
    }
}
