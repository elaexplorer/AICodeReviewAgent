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
var openAiApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
var azureOpenAiEndpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
var azureOpenAiApiKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
var azureOpenAiDeployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT") ?? "gpt-4";
var adoOrganization = Environment.GetEnvironmentVariable("ADO_ORGANIZATION") ?? "SPOOL";
var adoPat = Environment.GetEnvironmentVariable("ADO_PAT");
var mcpServerUrl = Environment.GetEnvironmentVariable("MCP_SERVER_URL") ?? "http://localhost:3000";

// Check if either OpenAI or Azure OpenAI is configured
var hasOpenAI = !string.IsNullOrEmpty(openAiApiKey);
var hasAzureOpenAI = !string.IsNullOrEmpty(azureOpenAiEndpoint) && !string.IsNullOrEmpty(azureOpenAiApiKey);

if ((!hasOpenAI && !hasAzureOpenAI) || string.IsNullOrEmpty(adoPat))
{
    Console.WriteLine("❌ Missing required environment variables:");
    if (!hasOpenAI && !hasAzureOpenAI)
    {
        Console.WriteLine("   Either configure OpenAI:");
        Console.WriteLine("     - OPENAI_API_KEY");
        Console.WriteLine("   Or configure Azure OpenAI:");
        Console.WriteLine("     - AZURE_OPENAI_ENDPOINT");
        Console.WriteLine("     - AZURE_OPENAI_API_KEY");
        Console.WriteLine("     - AZURE_OPENAI_DEPLOYMENT (optional, defaults to 'gpt-4')");
    }
    if (string.IsNullOrEmpty(adoPat)) Console.WriteLine("   - ADO_PAT");
    Console.WriteLine();
    Console.WriteLine("Usage: CodeReviewAgent <repository> <pullRequestId>");
    Console.WriteLine("       CodeReviewAgent <project> <repository> <pullRequestId>");
    Console.WriteLine("Example: CodeReviewAgent my-repo 123");
    Console.WriteLine("Example: CodeReviewAgent SCC my-repo 123");
    Console.WriteLine();
    Console.WriteLine("Required environment variables:");
    Console.WriteLine();
    Console.WriteLine("Option 1 - OpenAI:");
    Console.WriteLine("- OPENAI_API_KEY: Your OpenAI API key");
    Console.WriteLine();
    Console.WriteLine("Option 2 - Azure OpenAI:");
    Console.WriteLine("- AZURE_OPENAI_ENDPOINT: Your Azure OpenAI endpoint (e.g., https://your-resource.openai.azure.com/)");
    Console.WriteLine("- AZURE_OPENAI_API_KEY: Your Azure OpenAI API key");
    Console.WriteLine("- AZURE_OPENAI_DEPLOYMENT: Your deployment name (optional, defaults to 'gpt-4')");
    Console.WriteLine();
    Console.WriteLine("Common:");
    Console.WriteLine("- ADO_ORGANIZATION: Your Azure DevOps organization name (optional, defaults to 'SPOOL')");
    Console.WriteLine("- ADO_PAT: Your Azure DevOps Personal Access Token");
    Console.WriteLine("- MCP_SERVER_URL: MCP server URL (optional, defaults to http://localhost:3000)");
    return;
}

// Add services
builder.Services.AddLogging(config => config.AddConsole().SetMinimumLevel(LogLevel.Information));

// Add Semantic Kernel with appropriate AI service
var kernelBuilder = builder.Services.AddKernel();

if (hasAzureOpenAI)
{
    Console.WriteLine($"Using Azure OpenAI with deployment: {azureOpenAiDeployment}");
    kernelBuilder.AddAzureOpenAIChatCompletion(
        deploymentName: azureOpenAiDeployment,
        endpoint: azureOpenAiEndpoint!,
        apiKey: azureOpenAiApiKey!);
}
else if (hasOpenAI)
{
    Console.WriteLine("Using OpenAI with model: gpt-4");
    kernelBuilder.AddOpenAIChatCompletion("gpt-4", openAiApiKey!);
}

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

// Parse command-line arguments
string project;
string repository;
int pullRequestId;

if (args.Length == 2)
{
    // Format: <repository> <pullRequestId>
    // Use default project "SCC"
    project = "SCC";
    repository = args[0];
    if (!int.TryParse(args[1], out pullRequestId))
    {
        logger.LogError("Invalid pull request ID: {PullRequestId}", args[1]);
        logger.LogInformation("Usage: CodeReviewAgent <repository> <pullRequestId>");
        logger.LogInformation("       CodeReviewAgent <project> <repository> <pullRequestId>");
        logger.LogInformation("Example: CodeReviewAgent my-repo 123");
        logger.LogInformation("Example: CodeReviewAgent SCC my-repo 123");
        return;
    }
}
else if (args.Length >= 3)
{
    // Format: <project> <repository> <pullRequestId>
    project = args[0];
    repository = args[1];
    if (!int.TryParse(args[2], out pullRequestId))
    {
        logger.LogError("Invalid pull request ID: {PullRequestId}", args[2]);
        logger.LogInformation("Usage: CodeReviewAgent <repository> <pullRequestId>");
        logger.LogInformation("       CodeReviewAgent <project> <repository> <pullRequestId>");
        logger.LogInformation("Example: CodeReviewAgent my-repo 123");
        logger.LogInformation("Example: CodeReviewAgent SCC my-repo 123");
        return;
    }
}
else
{
    logger.LogInformation("Usage: CodeReviewAgent <repository> <pullRequestId>");
    logger.LogInformation("       CodeReviewAgent <project> <repository> <pullRequestId>");
    logger.LogInformation("Example: CodeReviewAgent my-repo 123");
    logger.LogInformation("Example: CodeReviewAgent SCC my-repo 123");
    logger.LogInformation("\nDefaults:");
    logger.LogInformation("- Project: SCC (if not specified)");
    logger.LogInformation("- Organization: SPOOL (if ADO_ORGANIZATION not set)");
    logger.LogInformation("\nRequired environment variables:");
    logger.LogInformation("\nOption 1 - OpenAI:");
    logger.LogInformation("- OPENAI_API_KEY: Your OpenAI API key");
    logger.LogInformation("\nOption 2 - Azure OpenAI:");
    logger.LogInformation("- AZURE_OPENAI_ENDPOINT: Your Azure OpenAI endpoint (e.g., https://your-resource.openai.azure.com/)");
    logger.LogInformation("- AZURE_OPENAI_API_KEY: Your Azure OpenAI API key");
    logger.LogInformation("- AZURE_OPENAI_DEPLOYMENT: Your deployment name (optional, defaults to 'gpt-4')");
    logger.LogInformation("\nCommon:");
    logger.LogInformation("- ADO_ORGANIZATION: Your Azure DevOps organization name (optional, defaults to 'SPOOL')");
    logger.LogInformation("- ADO_PAT: Your Azure DevOps Personal Access Token");
    logger.LogInformation("- MCP_SERVER_URL: MCP server URL (optional, defaults to http://localhost:3000)");
    return;
}

// Execute code review
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

logger.LogInformation("Code Review Agent finished");
