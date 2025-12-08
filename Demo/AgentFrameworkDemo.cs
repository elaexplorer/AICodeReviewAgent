using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace CodeReviewAgent.Demo;

/// <summary>
/// Demonstrates key features of Microsoft.Agents.AI framework
/// </summary>
public class AgentFrameworkDemo
{
    private readonly IChatClient _chatClient;
    private readonly ILogger<AgentFrameworkDemo> _logger;

    public AgentFrameworkDemo(IChatClient chatClient, ILogger<AgentFrameworkDemo> logger)
    {
        _chatClient = chatClient;
        _logger = logger;
    }

    /// <summary>
    /// FEATURE 1: Creating an Agent with Microsoft.Agents.AI
    ///
    /// This demonstrates how to create a ChatClientAgent with:
    /// - Specialized instructions (agent's personality and expertise)
    /// - A name for identification
    /// - Built on top of IChatClient from Microsoft.Extensions.AI
    /// </summary>
    public AIAgent CreateSpecializedAgent()
    {
        _logger.LogInformation("=== DEMO 1: Creating an Agent ===");

        // Create a ChatClientAgent with specialized instructions
        var agent = new ChatClientAgent(
            _chatClient,
            instructions: """
                You are a senior code reviewer specializing in C# and .NET.
                You provide constructive feedback focusing on:
                - Code quality and maintainability
                - Security vulnerabilities
                - Performance optimizations
                - Best practices and design patterns

                Be concise but thorough in your reviews.
                Always explain WHY something is an issue, not just WHAT is wrong.
                """,
            name: "CodeReviewExpert");

        _logger.LogInformation("✅ Created agent: {AgentName}", "CodeReviewExpert");
        _logger.LogInformation("   Agent Type: ChatClientAgent");
        _logger.LogInformation("   Built on: IChatClient (Microsoft.Extensions.AI)");
        _logger.LogInformation("   Instructions: Specialized for code review");

        return agent;
    }

    /// <summary>
    /// FEATURE 2: Context Management (Conversation History)
    ///
    /// This demonstrates how Microsoft.Agents.AI manages conversation context:
    /// - AgentRunResponse contains Messages property with full conversation history
    /// - Each RunAsync call can maintain context from previous interactions
    /// - Useful for multi-turn conversations where the agent needs to remember context
    /// </summary>
    public async Task DemonstrateContextManagement()
    {
        _logger.LogInformation("\n=== DEMO 2: Context Management (Conversation History) ===");

        // Create an agent for this demo
        var agent = new ChatClientAgent(
            _chatClient,
            instructions: "You are a helpful coding assistant. Remember previous context in the conversation.",
            name: "ContextAwareAgent");

        _logger.LogInformation("✅ Created context-aware agent");

        // First interaction - Ask about a topic
        _logger.LogInformation("\n📨 First interaction: Asking about async/await");
        var response1 = await agent.RunAsync("What is async/await in C#? Be brief.");

        _logger.LogInformation("🤖 Agent response: {Response}", response1.Text.Substring(0, Math.Min(100, response1.Text.Length)) + "...");
        _logger.LogInformation("📊 Messages in conversation: {Count}", response1.Messages.Count);

        // Access conversation history
        _logger.LogInformation("\n🔍 Conversation History:");
        for (int i = 0; i < response1.Messages.Count; i++)
        {
            var msg = response1.Messages[i];
            _logger.LogInformation("   Message {Index} - Role: {Role}, Content Length: {Length}",
                i + 1, msg.Role, msg.Text?.Length ?? 0);
        }

        // Second interaction - Follow up question (context-dependent)
        _logger.LogInformation("\n📨 Second interaction: Follow-up question (relies on previous context)");
        var response2 = await agent.RunAsync("Can you show me a simple example?");

        _logger.LogInformation("🤖 Agent response: {Response}", response2.Text.Substring(0, Math.Min(100, response2.Text.Length)) + "...");
        _logger.LogInformation("📊 Messages in conversation: {Count}", response2.Messages.Count);
        _logger.LogInformation("   ✨ Notice: Agent remembered we were talking about async/await!");

        // Show the accumulated conversation history
        _logger.LogInformation("\n📝 Full Conversation History After 2 Turns:");
        for (int i = 0; i < response2.Messages.Count; i++)
        {
            var msg = response2.Messages[i];
            var preview = msg.Text?.Length > 50 ? msg.Text.Substring(0, 50) + "..." : msg.Text;
            _logger.LogInformation("   {Index}. [{Role}] {Preview}",
                i + 1, msg.Role, preview);
        }
    }

    /// <summary>
    /// FEATURE 3: Multi-Agent Orchestration
    ///
    /// This demonstrates how multiple agents can work together:
    /// - Each agent has specialized expertise
    /// - Agents can be coordinated by an orchestrator
    /// - Results from multiple agents can be aggregated
    /// </summary>
    public async Task DemonstrateMultiAgentOrchestration()
    {
        _logger.LogInformation("\n=== DEMO 3: Multi-Agent Orchestration ===");

        // Create multiple specialized agents
        var securityAgent = new ChatClientAgent(
            _chatClient,
            instructions: "You are a security expert. Focus ONLY on security vulnerabilities.",
            name: "SecurityExpert");

        var performanceAgent = new ChatClientAgent(
            _chatClient,
            instructions: "You are a performance expert. Focus ONLY on performance issues.",
            name: "PerformanceExpert");

        var qualityAgent = new ChatClientAgent(
            _chatClient,
            instructions: "You are a code quality expert. Focus ONLY on code maintainability and readability.",
            name: "QualityExpert");

        _logger.LogInformation("✅ Created 3 specialized agents:");
        _logger.LogInformation("   1. SecurityExpert - Focuses on security vulnerabilities");
        _logger.LogInformation("   2. PerformanceExpert - Focuses on performance issues");
        _logger.LogInformation("   3. QualityExpert - Focuses on code quality");

        // Sample code to review
        string codeToReview = """
            public async Task<string> GetUserData(string userId)
            {
                var sql = "SELECT * FROM Users WHERE Id = " + userId;
                var result = await Database.ExecuteAsync(sql);
                return result.ToString();
            }
            """;

        _logger.LogInformation("\n📄 Code to review:\n{Code}", codeToReview);

        // Run all agents in parallel (multi-agent orchestration)
        _logger.LogInformation("\n🚀 Running multi-agent review in parallel...");

        var reviewTasks = new[]
        {
            securityAgent.RunAsync($"Review this code for security issues only:\n{codeToReview}"),
            performanceAgent.RunAsync($"Review this code for performance issues only:\n{codeToReview}"),
            qualityAgent.RunAsync($"Review this code for quality issues only:\n{codeToReview}")
        };

        var responses = await Task.WhenAll(reviewTasks);

        _logger.LogInformation("\n📊 Multi-Agent Results:");
        _logger.LogInformation("   🔒 Security Review: {Preview}",
            responses[0].Text.Substring(0, Math.Min(80, responses[0].Text.Length)) + "...");
        _logger.LogInformation("   ⚡ Performance Review: {Preview}",
            responses[1].Text.Substring(0, Math.Min(80, responses[1].Text.Length)) + "...");
        _logger.LogInformation("   ✨ Quality Review: {Preview}",
            responses[2].Text.Substring(0, Math.Min(80, responses[2].Text.Length)) + "...");

        _logger.LogInformation("\n✅ Multi-agent orchestration complete!");
        _logger.LogInformation("   Each agent provided specialized feedback in parallel");
    }

    /// <summary>
    /// FEATURE 4: AgentRunResponse Properties
    ///
    /// This demonstrates the rich response object from agents:
    /// - Text: The main response text
    /// - Messages: Full conversation history
    /// - Additional metadata and properties
    /// </summary>
    public async Task DemonstrateAgentResponse()
    {
        _logger.LogInformation("\n=== DEMO 4: AgentRunResponse Properties ===");

        var agent = new ChatClientAgent(
            _chatClient,
            instructions: "You are a helpful assistant.",
            name: "DemoAgent");

        var response = await agent.RunAsync("Explain dependency injection in one sentence.");

        _logger.LogInformation("✅ AgentRunResponse Properties:");
        _logger.LogInformation("   📝 Text: {Text}", response.Text);
        _logger.LogInformation("   💬 Messages Count: {Count}", response.Messages.Count);
        _logger.LogInformation("   🔧 Response Type: {Type}", response.GetType().Name);

        _logger.LogInformation("\n🔍 Messages Breakdown:");
        foreach (var message in response.Messages)
        {
            _logger.LogInformation("      Role: {Role}, Content: {Content}",
                message.Role,
                message.Text?.Length > 50 ? message.Text.Substring(0, 50) + "..." : message.Text);
        }
    }

    /// <summary>
    /// Run all demos in sequence
    /// </summary>
    public async Task RunAllDemos()
    {
        _logger.LogInformation("🎬 Starting Microsoft.Agents.AI Framework Demos\n");

        // Demo 1: Creating an Agent
        CreateSpecializedAgent();

        // Demo 2: Context Management
        await DemonstrateContextManagement();

        // Demo 3: Multi-Agent Orchestration
        await DemonstrateMultiAgentOrchestration();

        // Demo 4: Agent Response
        await DemonstrateAgentResponse();

        _logger.LogInformation("\n\n🎉 All demos completed!");
    }
}
