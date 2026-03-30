namespace CodeReviewAgent.Agents;

/// <summary>
/// Provides focused review instructions for parallel security/bugs/performance passes.
/// Returns null for an unknown focus area so agents fall back to their default full review section.
/// </summary>
internal static class FocusPassHelper
{
    public const string Security = "security";
    public const string Bugs = "bugs";
    public const string Performance = "performance";

    /// <summary>
    /// Returns the review instructions block for the given focus area,
    /// or null if focusArea is null/unknown (caller should use its default full-review section).
    /// </summary>
    public static string? GetReviewInstructions(string? focusArea) => focusArea switch
    {
        Security => """
            THIS IS A DEDICATED SECURITY PASS. Identify ONLY security vulnerabilities:
            - Injection flaws (SQL, command, shell, LDAP, XPath)
            - Authentication or authorization bypass
            - Hardcoded credentials, secrets, or API keys
            - Insecure deserialization or unsafe object handling
            - Sensitive data or PII written to logs or responses
            - Path traversal, SSRF, open redirect
            - Cryptographic weaknesses or misuse of random numbers
            Do NOT report bugs, performance issues, style problems, or anything outside security in this pass.
            """,

        Bugs => """
            THIS IS A DEDICATED BUG PASS. Identify ONLY defects and incorrect behaviour:
            - Logic errors and off-by-one mistakes
            - Null/None dereferences and unhandled edge cases
            - Resource leaks (unclosed handles, connections, streams)
            - Race conditions and thread-safety issues
            - Incorrect API usage or wrong assumptions about return values
            - Missing or swallowed error handling on critical paths
            Do NOT report security, performance, or style issues in this pass.
            """,

        Performance => """
            THIS IS A DEDICATED PERFORMANCE PASS. Identify ONLY performance and resource issues:
            - Inefficient algorithms or wrong data structures (e.g. O(n²) where O(n) is feasible)
            - Unnecessary allocations, copies, cloning, or boxing
            - N+1 query patterns or expensive calls inside loops
            - Blocking operations in async or concurrent paths
            - Redundant or repeated computation that could be cached
            Do NOT report security, bugs, or style issues in this pass.
            """,

        _ => null
    };
}
