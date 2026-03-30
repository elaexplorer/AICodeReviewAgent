using CodeReviewAgent.Services;
using FluentAssertions;
using Xunit;

namespace CodeReviewAgent.Tests.UnitTests.Services;

/// <summary>
/// Tests for DiffAnnotator.AnnotateDiffWithLineNumbers.
///
/// Bug reproduced: agents were reporting wrong line numbers because they had to mentally
/// count from the @@ hunk header. AnnotateDiffWithLineNumbers embeds [Lxx] tags on every
/// '+' line so the LLM can read the line number directly instead of computing it.
/// </summary>
public class DiffAnnotatorTests
{
    // -----------------------------------------------------------------------
    // Null / empty guard
    // -----------------------------------------------------------------------

    [Fact]
    public void AnnotateDiffWithLineNumbers_NullInput_ReturnsEmpty()
    {
        var result = DiffAnnotator.AnnotateDiffWithLineNumbers(null);
        result.Should().BeEmpty();
    }

    [Fact]
    public void AnnotateDiffWithLineNumbers_EmptyInput_ReturnsEmpty()
    {
        var result = DiffAnnotator.AnnotateDiffWithLineNumbers(string.Empty);
        result.Should().BeEmpty();
    }

    [Fact]
    public void AnnotateDiffWithLineNumbers_WhitespaceInput_ReturnsEmpty()
    {
        var result = DiffAnnotator.AnnotateDiffWithLineNumbers("   \n  ");
        result.Should().BeEmpty();
    }

    // -----------------------------------------------------------------------
    // Core bug regression: added lines must carry [Lxx] tags
    // -----------------------------------------------------------------------

    [Fact]
    public void AnnotateDiffWithLineNumbers_AddedLines_AnnotatedWithCorrectLineNumbers()
    {
        // Arrange — hunk starts at right-side line 10
        var diff = "@@ -8,3 +10,5 @@\n context\n+added line A\n+added line B\n context";

        // Act
        var result = DiffAnnotator.AnnotateDiffWithLineNumbers(diff);
        var lines = result.Split('\n');

        // Assert — "+added line A" must become "+[L11]added line A"
        // because the first context line consumes L10, then A=L11, B=L12
        lines.Should().Contain(l => l.TrimEnd('\r') == "+[L11]added line A",
            "first added line is at right-side line 11");
        lines.Should().Contain(l => l.TrimEnd('\r') == "+[L12]added line B",
            "second added line is at right-side line 12");
    }

    [Fact]
    public void AnnotateDiffWithLineNumbers_HunkStartsAtLine1_FirstAddedLineIsL1()
    {
        // A brand-new file: hunk is @@ -0,0 +1,2 @@
        var diff = "@@ -0,0 +1,2 @@\n+first line\n+second line";

        var result = DiffAnnotator.AnnotateDiffWithLineNumbers(diff);
        var lines = result.Split('\n');

        lines.Should().Contain(l => l.TrimEnd('\r') == "+[L1]first line");
        lines.Should().Contain(l => l.TrimEnd('\r') == "+[L2]second line");
    }

    // -----------------------------------------------------------------------
    // Deleted lines must NOT get annotations and must NOT advance right counter
    // -----------------------------------------------------------------------

    [Fact]
    public void AnnotateDiffWithLineNumbers_DeletedLines_NotAnnotatedAndDoNotAdvanceCounter()
    {
        // The '-' line is deleted — it has no right-side line number.
        // The '+' line that follows must still start at the correct right-side line.
        var diff = "@@ -5,3 +5,3 @@\n context\n-deleted line\n+added line\n context";

        var result = DiffAnnotator.AnnotateDiffWithLineNumbers(diff);
        var lines = result.Split('\n');

        // '-' line stays unchanged
        lines.Should().Contain(l => l.TrimEnd('\r') == "-deleted line");
        // '+' line gets L6 (context was L5, deleted line does NOT advance counter)
        lines.Should().Contain(l => l.TrimEnd('\r') == "+[L6]added line",
            "deleted lines do not consume a right-side line number");
    }

    // -----------------------------------------------------------------------
    // Context lines must NOT get annotations but DO advance the counter
    // -----------------------------------------------------------------------

    [Fact]
    public void AnnotateDiffWithLineNumbers_ContextLines_NotAnnotatedButAdvanceCounter()
    {
        // hunk starts at +20; two context lines then one added line
        var diff = "@@ -20,4 +20,5 @@\n context 1\n context 2\n+added\n context 3";

        var result = DiffAnnotator.AnnotateDiffWithLineNumbers(diff);
        var lines = result.Split('\n');

        lines.Should().Contain(l => l.TrimEnd('\r') == " context 1", "context lines are unchanged");
        lines.Should().Contain(l => l.TrimEnd('\r') == " context 2", "context lines are unchanged");
        // L20 = context 1, L21 = context 2, L22 = added
        lines.Should().Contain(l => l.TrimEnd('\r') == "+[L22]added");
    }

    // -----------------------------------------------------------------------
    // Multi-hunk diffs: each hunk resets the counter independently
    // -----------------------------------------------------------------------

    [Fact]
    public void AnnotateDiffWithLineNumbers_MultiHunkDiff_EachHunkUsesItsOwnStartLine()
    {
        var diff =
            "@@ -1,3 +1,4 @@\n context\n+hunk1 added\n context\n" +
            "@@ -50,3 +51,4 @@\n context\n+hunk2 added\n context";

        var result = DiffAnnotator.AnnotateDiffWithLineNumbers(diff);
        var lines = result.Split('\n');

        // Hunk 1: right starts at 1, context=L1, added=L2
        lines.Should().Contain(l => l.TrimEnd('\r') == "+[L2]hunk1 added");
        // Hunk 2: right starts at 51, context=L51, added=L52
        lines.Should().Contain(l => l.TrimEnd('\r') == "+[L52]hunk2 added");
    }

    // -----------------------------------------------------------------------
    // File header lines (--- / +++) must pass through unchanged
    // -----------------------------------------------------------------------

    [Fact]
    public void AnnotateDiffWithLineNumbers_FileHeaderLines_PassedThroughUnchanged()
    {
        var diff =
            "--- a/src/Foo.cs\n+++ b/src/Foo.cs\n@@ -1,2 +1,3 @@\n context\n+added line\n context";

        var result = DiffAnnotator.AnnotateDiffWithLineNumbers(diff);
        var lines = result.Split('\n');

        lines.Should().Contain(l => l.TrimEnd('\r') == "--- a/src/Foo.cs");
        lines.Should().Contain(l => l.TrimEnd('\r') == "+++ b/src/Foo.cs");
        // Added line inside the hunk still gets annotated correctly
        lines.Should().Contain(l => l.TrimEnd('\r') == "+[L2]added line");
    }

    // -----------------------------------------------------------------------
    // Hunk with no count (,N omitted) — e.g. @@ -5 +5,3 @@
    // -----------------------------------------------------------------------

    [Fact]
    public void AnnotateDiffWithLineNumbers_HunkHeaderWithoutCount_ParsedCorrectly()
    {
        var diff = "@@ -5 +5,3 @@\n+only added line";

        var result = DiffAnnotator.AnnotateDiffWithLineNumbers(diff);

        result.Should().Contain("+[L5]only added line");
    }

    // -----------------------------------------------------------------------
    // Edge: large line numbers render correctly
    // -----------------------------------------------------------------------

    [Fact]
    public void AnnotateDiffWithLineNumbers_LargeLineNumber_RenderedCorrectly()
    {
        var diff = "@@ -1000,2 +1000,3 @@\n context\n+big line number\n context";

        var result = DiffAnnotator.AnnotateDiffWithLineNumbers(diff);

        result.Should().Contain("+[L1001]big line number");
    }

    // -----------------------------------------------------------------------
    // Hunk header line itself must never be annotated
    // -----------------------------------------------------------------------

    [Fact]
    public void AnnotateDiffWithLineNumbers_HunkHeaderLine_NeverAnnotated()
    {
        var diff = "@@ -1,2 +1,3 @@\n context\n+added";

        var result = DiffAnnotator.AnnotateDiffWithLineNumbers(diff);
        var lines = result.Split('\n');

        lines.Should().NotContain(l => l.TrimStart().StartsWith("@@") && l.Contains("[L"));
    }
}
