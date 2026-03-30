using System.Text;
using System.Text.RegularExpressions;

namespace CodeReviewAgent.Services;

/// <summary>
/// Annotates unified diff output with explicit right-side line numbers so the LLM
/// does not have to count from hunk headers and can report accurate line numbers.
/// </summary>
public static class DiffAnnotator
{
    private static readonly Regex HunkHeaderRegex =
        new(@"@@ -\d+(?:,\d+)? \+(?<start>\d+)(?:,\d+)? @@", RegexOptions.Compiled);

    /// <summary>
    /// Rewrites each '+' line in a unified diff to include an [Lxx] tag containing
    /// the actual right-side (new-file) line number, e.g.:
    ///   +[L42] public void Foo() { ... }
    ///
    /// Context lines and '-' lines are left unchanged.
    /// The LLM is instructed to use the number inside [Lxx] as the lineNumber value.
    /// </summary>
    public static string AnnotateDiffWithLineNumbers(string? unifiedDiff)
    {
        if (string.IsNullOrWhiteSpace(unifiedDiff))
            return string.Empty;

        var sb = new StringBuilder();
        var currentRightLine = 0;
        var inHunk = false;

        foreach (var rawLine in unifiedDiff.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');

            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                var match = HunkHeaderRegex.Match(line);
                if (match.Success)
                {
                    currentRightLine = int.Parse(match.Groups["start"].Value);
                    inHunk = true;
                }
                else
                {
                    inHunk = false;
                }
                sb.AppendLine(line);
                continue;
            }

            if (!inHunk ||
                line.StartsWith("---", StringComparison.Ordinal) ||
                line.StartsWith("+++", StringComparison.Ordinal))
            {
                sb.AppendLine(line);
                continue;
            }

            if (line.StartsWith("-", StringComparison.Ordinal))
            {
                // Deleted line — no right-side line number, leave as-is
                sb.AppendLine(line);
                continue;
            }

            if (line.StartsWith("+", StringComparison.Ordinal))
            {
                // Added line — annotate with right-side line number
                sb.AppendLine($"+[L{currentRightLine}]{line.Substring(1)}");
                currentRightLine++;
            }
            else
            {
                // Context line — advance counter but leave text unchanged
                sb.AppendLine(line);
                currentRightLine++;
            }
        }

        return sb.ToString().TrimEnd();
    }
}
