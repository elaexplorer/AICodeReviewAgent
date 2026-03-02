namespace CodeReviewAgent.Services;

/// <summary>
/// Classifies review-time exceptions into actionable categories for user-facing errors.
/// </summary>
public static class ReviewExceptionClassifier
{
    public static bool IsAuthenticationError(Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            var message = current.Message ?? string.Empty;
            if (string.IsNullOrWhiteSpace(message))
            {
                continue;
            }

            if (message.Contains("HTTP 401", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("invalid subscription key", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("Access denied", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("wrong API endpoint", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
