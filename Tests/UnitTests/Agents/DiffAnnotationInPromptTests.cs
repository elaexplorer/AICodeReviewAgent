using System.Runtime.CompilerServices;
using CodeReviewAgent.Agents;
using CodeReviewAgent.Models;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CodeReviewAgent.Tests.UnitTests.Agents;

/// <summary>
/// Verifies that the [Lxx] diff annotation actually appears in the prompt
/// sent to the LLM by each review agent — i.e. the fix is wired end-to-end.
///
/// Uses a CapturingChatClient that records every message it receives so
/// we can assert on what the agent would send to Azure OpenAI without
/// requiring a live ADO connection.
/// </summary>
public class DiffAnnotationInPromptTests
{
    // Diff where hunk starts at right-side line 10:
    //   L10 = context, L11 = added, L12 = context
    private const string SampleDiff =
        "@@ -10,4 +10,4 @@\n" +
        " context line\n" +
        "+added security check\n" +
        "-removed old check\n" +
        " context after";

    [Fact]
    public async Task DotNetAgent_Prompt_ContainsDiffAnnotations()
    {
        var capturing = new CapturingChatClient("[{\"lineNumber\":11,\"severity\":\"high\",\"type\":\"issue\",\"comment\":\"test\"}]");
        var agent = new DotNetReviewAgent(NullLogger<DotNetReviewAgent>.Instance, capturing);

        await agent.ReviewFileAsync(MakeFile(".cs", SampleDiff), codebaseContext: "");

        capturing.LastPrompt.Should().Contain("+[L11]added security check",
            "the agent must annotate '+' lines with their right-side line number before sending to the LLM");
        capturing.LastPrompt.Should().NotContain("\n+added security check",
            "un-annotated '+' lines must not appear in the prompt");
    }

    [Fact]
    public async Task PythonAgent_Prompt_ContainsDiffAnnotations()
    {
        var capturing = new CapturingChatClient("[{\"lineNumber\":11,\"severity\":\"high\",\"type\":\"issue\",\"comment\":\"test\"}]");
        var agent = new PythonReviewAgent(NullLogger<PythonReviewAgent>.Instance, capturing);

        await agent.ReviewFileAsync(MakeFile(".py", SampleDiff), codebaseContext: "");

        capturing.LastPrompt.Should().Contain("+[L11]added security check");
        capturing.LastPrompt.Should().NotContain("\n+added security check");
    }

    [Fact]
    public async Task RustAgent_Prompt_ContainsDiffAnnotations()
    {
        var capturing = new CapturingChatClient("[{\"lineNumber\":11,\"severity\":\"high\",\"type\":\"issue\",\"comment\":\"test\"}]");
        var agent = new RustReviewAgent(NullLogger<RustReviewAgent>.Instance, capturing);

        await agent.ReviewFileAsync(MakeFile(".rs", SampleDiff), codebaseContext: "");

        capturing.LastPrompt.Should().Contain("+[L11]added security check");
        capturing.LastPrompt.Should().NotContain("\n+added security check");
    }

    [Fact]
    public async Task DotNetAgent_ParsedLineNumber_MatchesAnnotatedTag()
    {
        // LLM responds with lineNumber 11 (the [L11] annotated line).
        // The parsed comment must carry exactly that line number.
        var capturing = new CapturingChatClient("[{\"lineNumber\":11,\"severity\":\"high\",\"type\":\"issue\",\"comment\":\"hardcoded secret\"}]");
        var agent = new DotNetReviewAgent(NullLogger<DotNetReviewAgent>.Instance, capturing);

        var comments = await agent.ReviewFileAsync(MakeFile(".cs", SampleDiff), codebaseContext: "");

        comments.Should().ContainSingle();
        comments[0].LineNumber.Should().Be(11,
            "the parsed lineNumber must match the [Lxx] tag that the LLM read from the annotated diff");
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static PullRequestFile MakeFile(string ext, string diff) => new()
    {
        Path = $"/src/Sample{ext}",
        ChangeType = "edit",
        Content = "// full file content",
        PreviousContent = "// previous content",
        UnifiedDiff = diff
    };

    // -----------------------------------------------------------------------
    // CapturingChatClient — records the last user message for assertion
    // -----------------------------------------------------------------------

    private sealed class CapturingChatClient : IChatClient
    {
        private readonly string _fixedResponse;
        public string LastPrompt { get; private set; } = string.Empty;

        public CapturingChatClient(string fixedResponse)
        {
            _fixedResponse = fixedResponse;
            ChatClientMetadata = new ChatClientMetadata("CapturingChatClient");
        }

        public ChatClientMetadata ChatClientMetadata { get; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            // Capture everything the agent sends to the LLM
            LastPrompt = string.Join("\n", chatMessages
                .SelectMany(m => m.Contents.OfType<TextContent>())
                .Select(t => t.Text));

            var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, _fixedResponse));
            return Task.FromResult(response);
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
