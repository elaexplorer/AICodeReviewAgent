using CodeReviewAgent.Agents;
using CodeReviewAgent.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

var builder = Host.CreateApplicationBuilder(args);

// Configuration validation
var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
var adoOrganization = Environment.GetEnvironmentVariable("ADO_ORGANIZATION");
var adoPat = Environment.GetEnvironmentVariable("ADO_PAT");
var mcpServerUrl = Environment.GetEnvironmentVariable("MCP_SERVER_URL") ?? "http://localhost:3000";

if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(adoOrganization) || string.IsNullOrEmpty(adoPat))
{
    Console.WriteLine("❌ Missing required environment variables:");
    if (string.IsNullOrEmpty(apiKey)) Console.WriteLine("   - OPENAI_API_KEY");
    if (string.IsNullOrEmpty(adoOrganization)) Console.WriteLine("   - ADO_ORGANIZATION");
    if (string.IsNullOrEmpty(adoPat)) Console.WriteLine("   - ADO_PAT");
    Console.WriteLine();
    Console.WriteLine("Usage: CodeReviewAgent <project> <repository> <pullRequestId>");
    Console.WriteLine("Example: CodeReviewAgent SCC my-repo 123");
    Console.WriteLine();
    Console.WriteLine("Required environment variables:");
    Console.WriteLine("- OPENAI_API_KEY: Your OpenAI API key");
    Console.WriteLine("- ADO_ORGANIZATION: Your Azure DevOps organization name");
    Console.WriteLine("- ADO_PAT: Your Azure DevOps Personal Access Token");
    Console.WriteLine("- MCP_SERVER_URL: MCP server URL (optional, defaults to http://localhost:3000)");
    return;
}

// Add services
builder.Services.AddLogging(config => config.AddConsole().SetMinimumLevel(LogLevel.Information));

// Add Semantic Kernel
builder.Services.AddKernel()
    .AddOpenAIChatCompletion("gpt-4", apiKey);

// Add custom services
builder.Services.AddSingleton<CodebaseCache>();

builder.Services.AddSingleton(provider =>
    new AzureDevOpsRestClient(
        provider.GetRequiredService<ILogger<AzureDevOpsRestClient>>(),
        adoOrganization,
        adoPat));

builder.Services.AddSingleton(provider =>
    new AzureDevOpsMcpClient(
        provider.GetRequiredService<ILogger<AzureDevOpsMcpClient>>(),
        adoOrganization,
        adoPat,
        args.Length > 0 ? args[0] : "",
        provider.GetRequiredService<AzureDevOpsRestClient>(),
        provider.GetRequiredService<CodebaseCache>()));

// Add language-specific review agents
builder.Services.AddSingleton<ILanguageReviewAgent, PythonReviewAgent>(provider =>
    new PythonReviewAgent(
        provider.GetRequiredService<ILogger<PythonReviewAgent>>(),
        provider.GetRequiredService<Kernel>()));

builder.Services.AddSingleton<ILanguageReviewAgent, DotNetReviewAgent>(provider =>
    new DotNetReviewAgent(
        provider.GetRequiredService<ILogger<DotNetReviewAgent>>(),
        provider.GetRequiredService<Kernel>()));

builder.Services.AddSingleton<ILanguageReviewAgent, RustReviewAgent>(provider =>
    new RustReviewAgent(
        provider.GetRequiredService<ILogger<RustReviewAgent>>(),
        provider.GetRequiredService<Kernel>()));

// Add orchestrator
builder.Services.AddSingleton<CodeReviewOrchestrator>();

builder.Services.AddSingleton<CodeReviewService>();
builder.Services.AddSingleton<CodeReviewAgentService>();

var app = builder.Build();

// Get required services
var logger = app.Services.GetRequiredService<ILogger<Program>>();
var codeReviewAgent = app.Services.GetRequiredService<CodeReviewAgentService>();

logger.LogInformation("Code Review Agent started");

// Example usage
if (args.Length >= 3)
{
    var project = args[0];
    var repository = args[1];
    if (int.TryParse(args[2], out var pullRequestId))
    {
        logger.LogInformation("Reviewing PR {PullRequestId} in repository {Repository} (project {Project})", pullRequestId, repository, project);

        var success = await codeReviewAgent.ReviewPullRequestAsync(project, repository, pullRequestId);

        if (success)
        {
            logger.LogInformation("Code review completed successfully");

            // Get and display summary
            var summary = await codeReviewAgent.GetReviewSummaryAsync(project, repository, pullRequestId);
            Console.WriteLine("\n" + summary);
        }
        else
        {
            logger.LogError("Code review failed");
        }
    }
    else
    {
        logger.LogError("Invalid pull request ID: {PullRequestId}", args[2]);
    }
}
else
{
    logger.LogInformation("Usage: CodeReviewAgent <project> <repository> <pullRequestId>");
    logger.LogInformation("Example: CodeReviewAgent SCC my-repo 123");
    logger.LogInformation("\nRequired environment variables:");
    logger.LogInformation("- OPENAI_API_KEY: Your OpenAI API key");
    logger.LogInformation("- ADO_ORGANIZATION: Your Azure DevOps organization name");
    logger.LogInformation("- ADO_PAT: Your Azure DevOps Personal Access Token");
    logger.LogInformation("- MCP_SERVER_URL: MCP server URL (optional, defaults to http://localhost:3000)");
}

logger.LogInformation("Code Review Agent finished");
