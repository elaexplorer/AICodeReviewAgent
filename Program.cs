using CodeReviewAgent.Agents;
using CodeReviewAgent.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.AI;
using Azure.AI.OpenAI;
using Azure;
using OpenAI;
using System.ClientModel;
using OpenTelemetry.Trace;
using OpenTelemetry.Resources;

// Create web application builder
var builder = WebApplication.CreateBuilder(args);

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

// Add OpenTelemetry for observability
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("CodeReviewAgent"))
    .WithTracing(tracing => tracing
        .AddSource("Microsoft.Extensions.AI")
        .AddHttpClientInstrumentation()
        .AddConsoleExporter());

// Register IChatClient with middleware pipeline
if (hasAzureOpenAI)
{
    Console.WriteLine($"Using Azure OpenAI with deployment: {azureOpenAiDeployment}");

    builder.Services.AddSingleton<IChatClient>(provider =>
    {
        var azureClient = new AzureOpenAIClient(
            new Uri(azureOpenAiEndpoint!),
            new AzureKeyCredential(azureOpenAiApiKey!));

        // Get ChatClient and convert to IChatClient using AsIChatClient()
        var chatClient = azureClient.GetChatClient(azureOpenAiDeployment);
        return chatClient.AsIChatClient();
    });

    // Register embedding generator for RAG
    builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(provider =>
    {
        var azureClient = new AzureOpenAIClient(
            new Uri(azureOpenAiEndpoint!),
            new AzureKeyCredential(azureOpenAiApiKey!));

        // Get EmbeddingClient and convert to IEmbeddingGenerator
        var embeddingClient = azureClient.GetEmbeddingClient("text-embedding-ada-002");
        return embeddingClient.AsIEmbeddingGenerator();
    });
}
else if (hasOpenAI)
{
    Console.WriteLine("Using OpenAI with model: gpt-4");

    builder.Services.AddSingleton<IChatClient>(provider =>
    {
        var openAIClient = new OpenAIClient(openAiApiKey!);
        // Get ChatClient and convert to IChatClient using AsIChatClient()
        var chatClient = openAIClient.GetChatClient("gpt-4");
        return chatClient.AsIChatClient();
    });

    builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(provider =>
    {
        var openAIClient = new OpenAIClient(openAiApiKey!);
        // Get EmbeddingClient and convert to IEmbeddingGenerator
        var embeddingClient = openAIClient.GetEmbeddingClient("text-embedding-ada-002");
        return embeddingClient.AsIEmbeddingGenerator();
    });
}

// Add distributed cache for response caching (in-memory for now)
builder.Services.AddDistributedMemoryCache();

// Add custom services
builder.Services.AddSingleton<CodebaseCache>();
builder.Services.AddSingleton<AdoConfigurationService>();

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

// Add RAG context service
builder.Services.AddSingleton<CodebaseContextService>();

// Add language-specific review agents (simplified - no Kernel dependency)
builder.Services.AddSingleton<ILanguageReviewAgent, PythonReviewAgent>();
builder.Services.AddSingleton<ILanguageReviewAgent, DotNetReviewAgent>();
builder.Services.AddSingleton<ILanguageReviewAgent, RustReviewAgent>();

// Add orchestrator
builder.Services.AddSingleton<CodeReviewOrchestrator>();

builder.Services.AddSingleton<CodeReviewService>();
builder.Services.AddSingleton<CodeReviewAgentService>();

// Add web services
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var app = builder.Build();

// Configure middleware
app.UseCors();
app.UseDefaultFiles();

// Disable caching for static files during development
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        ctx.Context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
        ctx.Context.Response.Headers["Pragma"] = "no-cache";
        ctx.Context.Response.Headers["Expires"] = "0";
    }
});

app.MapControllers();

// Get required services
var logger = app.Services.GetRequiredService<ILogger<Program>>();
var codeReviewAgent = app.Services.GetRequiredService<CodeReviewAgentService>();

// Check if running in web mode (no command-line arguments)
if (args.Length == 0 || args.Contains("--web"))
{
    logger.LogInformation("Starting Code Review Agent in Web UI mode");
    logger.LogInformation("Open http://localhost:5000 in your browser");
    app.Run("http://localhost:5000");
    return;
}

logger.LogInformation("Code Review Agent started in CLI mode");

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
