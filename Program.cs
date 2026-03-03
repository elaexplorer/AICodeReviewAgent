using CodeReviewAgent.Agents;
using CodeReviewAgent.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.AI;
using Azure.AI.OpenAI;
using Azure;
using System.ClientModel;
using OpenTelemetry.Trace;
using OpenTelemetry.Resources;
using DotNetEnv;

// Load .env file if it exists
var envPath = Path.Combine(Directory.GetCurrentDirectory(), ".env");
if (File.Exists(envPath))
{
    Console.WriteLine($"✓ Loading environment variables from .env file");
    Env.Load(envPath);
}
else
{
    Console.WriteLine($"ℹ No .env file found at {envPath}, using system environment variables");
}

// Create web application builder
var builder = WebApplication.CreateBuilder(args);

// Helper function to get environment variable with fallback
string? GetEnvVar(string key) => Environment.GetEnvironmentVariable(key);

bool IsForceUiConfigEnabled()
{
    var value = Environment.GetEnvironmentVariable("FORCE_UI_CONFIG");
    if (string.IsNullOrWhiteSpace(value))
    {
        return false;
    }

    return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
}

var forceUiConfig = IsForceUiConfigEnabled();

// Configuration validation - now uses .env values first, then system variables
var openAiApiKey = GetEnvVar("OPENAI_API_KEY");
var azureOpenAiEndpoint = GetEnvVar("AZURE_OPENAI_ENDPOINT");
var azureOpenAiApiKey = GetEnvVar("AZURE_OPENAI_API_KEY");
var azureOpenAiDeployment = GetEnvVar("AZURE_OPENAI_DEPLOYMENT") ?? "gpt-4";
var azureOpenAiEmbeddingDeployment = GetEnvVar("AZURE_OPENAI_EMBEDDING_DEPLOYMENT") ?? "text-embedding-ada-002";
// Support separate endpoint for embeddings (some orgs have different resources for chat vs embeddings)
var azureOpenAiEmbeddingEndpoint = GetEnvVar("AZURE_OPENAI_EMBEDDING_ENDPOINT") ?? azureOpenAiEndpoint;
var adoOrganization = forceUiConfig ? string.Empty : (GetEnvVar("ADO_ORGANIZATION") ?? "SPOOL");
var adoPat = forceUiConfig ? string.Empty : (GetEnvVar("ADO_PAT") ?? string.Empty);
var mcpServerUrl = GetEnvVar("MCP_SERVER_URL") ?? "http://localhost:3000";

if (forceUiConfig)
{
    Console.WriteLine("FORCE_UI_CONFIG enabled: environment credentials are ignored until user submits config in UI.");
}

// Add runtime logger service first
builder.Services.AddSingleton<RuntimeLoggerService>();
builder.Services.AddHostedService<RuntimeLoggerService>(provider => provider.GetRequiredService<RuntimeLoggerService>());

// Add services with both console and runtime file logging
builder.Services.AddLogging(config => 
{
    config.AddConsole().SetMinimumLevel(LogLevel.Debug);
    
    // Add runtime file logging
    config.Services.AddSingleton<ILoggerProvider>(provider =>
        new RuntimeFileLoggerProvider(provider.GetRequiredService<RuntimeLoggerService>()));
});

// Add OpenTelemetry for observability
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("CodeReviewAgent"))
    .WithTracing(tracing => tracing
        .AddSource("Microsoft.Extensions.AI")
        .AddHttpClientInstrumentation()
        .AddConsoleExporter());

// Register chat and embedding providers with runtime configuration.
builder.Services.AddSingleton<ChatConfigurationService>();
builder.Services.AddSingleton<IChatClient, DynamicChatClient>();

Console.WriteLine($"Configured default chat deployment: {azureOpenAiDeployment}");
Console.WriteLine($"Configured default embedding model: {azureOpenAiEmbeddingDeployment}");
Console.WriteLine($"Configured default embedding endpoint: {azureOpenAiEmbeddingEndpoint}");

builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(provider =>
    new DynamicAzureEmbeddingGenerator(
        provider.GetRequiredService<EmbeddingConfigurationService>(),
        provider.GetRequiredService<ILogger<DynamicAzureEmbeddingGenerator>>()));

// Add distributed cache for response caching (in-memory for now)
builder.Services.AddDistributedMemoryCache();

// Add custom services
builder.Services.AddSingleton<CodebaseCache>();
builder.Services.AddSingleton<AdoConfigurationService>();
builder.Services.AddSingleton<EmbeddingConfigurationService>();

// Add embedding inspection and visualization services
builder.Services.AddEmbeddingInspection();
builder.Services.AddEmbeddingVisualization();
builder.Services.AddMemoryInspection();

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

// Clean up any existing temp clone directories from previous runs
CleanupTempCloneDirectories(app.Services.GetRequiredService<ILogger<Program>>());

// Initialize ADO configuration on startup if credentials are available
if (!forceUiConfig && !string.IsNullOrEmpty(adoOrganization) && !string.IsNullOrEmpty(adoPat))
{
    var adoConfig = app.Services.GetRequiredService<AdoConfigurationService>();
    var adoClient = app.Services.GetRequiredService<AzureDevOpsMcpClient>();
    var startupLogger = app.Services.GetRequiredService<ILogger<Program>>();
    
    startupLogger.LogInformation("Initializing ADO clients with credentials from environment...");
    
    var validationTask = adoConfig.ValidateAndConfigureAsync(adoOrganization, adoPat);
    var timeoutTask = Task.Delay(TimeSpan.FromSeconds(15));
    var completed = await Task.WhenAny(validationTask, timeoutTask);

    if (completed == timeoutTask)
    {
        startupLogger.LogWarning("⚠️  ADO PAT validation timed out after 15s - app will start anyway");
        startupLogger.LogWarning("   ADO connectivity will be retried on first PR review request");
        // Still configure with the credentials so the client can use them
        adoClient.UpdateConfiguration(adoOrganization, adoPat);
    }
    else
    {
        var (isValid, errorMessage) = await validationTask;
        if (isValid)
        {
            adoClient.UpdateConfiguration(adoOrganization, adoPat);
            startupLogger.LogInformation("✅ ADO clients initialized successfully for organization: {Organization}", adoOrganization);
        }
        else
        {
            startupLogger.LogWarning("⚠️  ADO PAT validation failed: {Error}", errorMessage);
            startupLogger.LogWarning("   You will need to log in manually through the web UI");
        }
    }
}

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

// Check for embedding inspection mode
if (args.Contains("--inspect-embeddings"))
{
    logger.LogInformation("Starting Embedding Inspector mode");
    await EmbeddingInspectionExtensions.RunEmbeddingInspectionAsync(app.Services, args);
    return;
}

// Check for embedding visualization mode
if (args.Contains("--visualize-embeddings"))
{
    logger.LogInformation("Starting Embedding Visualizer mode");
    await EmbeddingVisualizationExtensions.RunEmbeddingVisualizationAsync(app.Services, args);
    return;
}

// Check for test runner mode
if (args.Contains("--test-runner"))
{
    logger.LogWarning("Test runner mode is not available in the main app build. Run tests via 'dotnet test' or the test scripts.");
    Environment.Exit(1);
    return;
}

// Check for memory inspection mode
if (args.Contains("--inspect-memory"))
{
    logger.LogInformation("Starting Memory Inspector mode");
    await MemoryInspectionExtensions.RunMemoryInspectionAsync(app.Services, args);
    return;
}

// Check for git clone RAG test mode
if (args.Contains("--test-git-clone-rag"))
{
    logger.LogInformation("Starting Git Clone RAG Test mode");
    await TestGitCloneRagAsync(app.Services, args, logger);
    return;
}

// Check if running in web mode (no command-line arguments)
if (args.Length == 0 || args.Contains("--web"))
{
    logger.LogInformation("Starting Code Review Agent in Web UI mode");
    // Use ASPNETCORE_URLS environment variable or default to http://localhost:5001
    var urls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "http://localhost:5001";
    logger.LogInformation("Listening on: {Urls}", urls);
    app.Run();
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

/// <summary>
/// Clean up any existing temp clone directories from previous runs to ensure fresh state
/// </summary>
static void CleanupTempCloneDirectories(ILogger<Program> logger)
{
    try
    {
        // Clean up old temp location
        var oldTempPath = Path.Combine(Path.GetTempPath(), "repo_clone");
        if (Directory.Exists(oldTempPath))
        {
            logger.LogInformation("🧹 Cleaning up old temp clone directories from: {TempPath}", oldTempPath);
            ForceDeleteDirectory(oldTempPath, logger);
            logger.LogInformation("✅ Old temp clone directories cleaned successfully");
        }

        // Clean up new temp location in codebase
        var currentDir = Directory.GetCurrentDirectory();
        var tempReposDir = Path.Combine(currentDir, "temp_repos");
        if (Directory.Exists(tempReposDir))
        {
            logger.LogInformation("🧹 Cleaning up temp_repos from codebase: {TempPath}", tempReposDir);
            ForceDeleteDirectory(tempReposDir, logger);
            logger.LogInformation("✅ temp_repos cleaned successfully");
        }
        else
        {
            logger.LogDebug("ℹ️ No temp clone directories found to clean");
        }
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "⚠️ Failed to clean up temp clone directories - continuing anyway");
    }
}

static void ForceDeleteDirectory(string directoryPath, ILogger<Program> logger)
{
    if (!Directory.Exists(directoryPath))
    {
        return;
    }

    const int maxAttempts = 5;
    for (int attempt = 1; attempt <= maxAttempts; attempt++)
    {
        try
        {
            Directory.Delete(directoryPath, recursive: true);
            return;
        }
        catch (UnauthorizedAccessException) when (attempt < maxAttempts)
        {
            logger.LogDebug("Directory cleanup access retry {Attempt}/{MaxAttempts} for {Path}", attempt, maxAttempts, directoryPath);
        }
        catch (IOException) when (attempt < maxAttempts)
        {
            logger.LogDebug("Directory cleanup IO retry {Attempt}/{MaxAttempts} for {Path}", attempt, maxAttempts, directoryPath);
        }

        try
        {
            using var process = new System.Diagnostics.Process();
            process.StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-Command \"Remove-Item '{directoryPath}' -Recurse -Force -ErrorAction SilentlyContinue\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };

            process.Start();
            process.WaitForExit();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Fallback force delete attempt failed for {Path}", directoryPath);
        }

        if (!Directory.Exists(directoryPath))
        {
            return;
        }

        Thread.Sleep(250 * attempt);
    }

    // Final attempt - let exception bubble to caller for top-level warning.
    Directory.Delete(directoryPath, recursive: true);
}

/// <summary>
/// Test the git clone-based RAG indexing approach
/// </summary>
static async Task TestGitCloneRagAsync(IServiceProvider services, string[] args, ILogger<Program> logger)
{
    logger.LogInformation("🧪 TESTING GIT CLONE-BASED RAG INDEXING");
    logger.LogInformation("═══════════════════════════════════════");

    try
    {
        var codebaseService = services.GetRequiredService<CodebaseContextService>();
        
        // Test parameters - use generic sample defaults
        var project = "MyProject";
        var repositoryId = "my-repository";
        var branch = "master";
        var repositoryUrl = "https://dev.azure.com/your-organization/MyProject/_git/my-repository";

        logger.LogInformation("Test Parameters:");
        logger.LogInformation("  Project: {Project}", project);
        logger.LogInformation("  Repository: {Repository}", repositoryId);
        logger.LogInformation("  Branch: {Branch}", branch);
        logger.LogInformation("  Repository URL: {Url}", repositoryUrl);

        // Step 1: Test API-based indexing first for comparison
        logger.LogInformation("\n🔄 Step 1: Running API-based indexing for comparison...");
        var apiChunkCount = await codebaseService.IndexRepositoryAsync(project, repositoryId, branch);
        logger.LogInformation("✅ API-based indexing result: {ChunkCount} chunks", apiChunkCount);

        // Step 2: Test git clone-based indexing
        logger.LogInformation("\n🚀 Step 2: Running Git Clone-based indexing...");
        var cloneChunkCount = await codebaseService.IndexRepositoryWithCloneAsync(project, repositoryId, branch, repositoryUrl);
        logger.LogInformation("✅ Git Clone-based indexing result: {ChunkCount} chunks", cloneChunkCount);

        // Step 3: Compare results
        logger.LogInformation("\n📊 COMPARISON RESULTS:");
        logger.LogInformation("═════════════════════");
        logger.LogInformation("API-based chunks:   {ApiChunks}", apiChunkCount);
        logger.LogInformation("Git clone chunks:   {CloneChunks}", cloneChunkCount);
        logger.LogInformation("Improvement:        {Improvement} chunks ({Percentage:F1}% more)",
            cloneChunkCount - apiChunkCount,
            apiChunkCount > 0 ? ((double)(cloneChunkCount - apiChunkCount) / apiChunkCount) * 100 : 0);

        if (cloneChunkCount > apiChunkCount)
        {
            logger.LogInformation("🎉 SUCCESS: Git clone found {MoreChunks} more chunks than API approach!",
                cloneChunkCount - apiChunkCount);
            logger.LogInformation("   This proves git clone discovers more files in the repository.");
        }
        else if (cloneChunkCount == apiChunkCount)
        {
            logger.LogInformation("🤔 EQUAL: Both approaches found the same number of chunks.");
            logger.LogInformation("   Either the API pagination fix worked, or both have same coverage.");
        }
        else
        {
            logger.LogInformation("⚠️  UNEXPECTED: Git clone found fewer chunks than API approach.");
            logger.LogInformation("   This suggests an issue with the git clone implementation.");
        }

        // Step 4: Display index summary
        logger.LogInformation("\n📋 INDEX SUMMARY:");
        logger.LogInformation("════════════════");
        var summary = codebaseService.GetIndexSummary(repositoryId);
        logger.LogInformation("{Summary}", summary);

        logger.LogInformation("\n✅ Git Clone RAG test completed successfully!");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "❌ Git Clone RAG test failed");
    }
}
