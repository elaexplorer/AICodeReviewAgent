using Azure;
using Azure.AI.OpenAI;
using DotNetEnv;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;

namespace CodeReviewAgent.Tests.Evals;

/// <summary>
/// Shared configuration helper for eval tests.
/// Loads Azure OpenAI credentials from .env and builds IChatClient / ChatConfiguration.
/// </summary>
internal static class EvalConfiguration
{
    private static bool _envLoaded = false;
    private static readonly object _lock = new();

    private static void EnsureEnvLoaded()
    {
        if (_envLoaded) return;
        lock (_lock)
        {
            if (_envLoaded) return;
            // Walk up from bin/Debug/net9.0 to find the .env in the repo root
            var dir = AppContext.BaseDirectory;
            for (int i = 0; i < 7; i++)
            {
                var candidate = Path.Combine(dir, ".env");
                if (File.Exists(candidate))
                {
                    Env.Load(candidate);
                    break;
                }
                var parent = Path.GetDirectoryName(dir);
                if (parent == null) break;
                dir = parent;
            }
            _envLoaded = true;
        }
    }

    public static (string endpoint, string apiKey, string deployment) GetAzureOpenAISettings()
    {
        EnsureEnvLoaded();
        var endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")
            ?? throw new InvalidOperationException(
                "AZURE_OPENAI_ENDPOINT is not set. Ensure your .env file is configured.");
        var apiKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY")
            ?? throw new InvalidOperationException(
                "AZURE_OPENAI_API_KEY is not set. Ensure your .env file is configured.");
        var deployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT") ?? "gpt-4";
        return (endpoint, apiKey, deployment);
    }

    /// <summary>
    /// Creates an IChatClient backed by Azure OpenAI (used by the agent under test).
    /// </summary>
    public static IChatClient CreateChatClient()
    {
        var (endpoint, apiKey, deployment) = GetAzureOpenAISettings();
        var azureClient = new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(apiKey));
        return azureClient.GetChatClient(deployment).AsIChatClient();
    }

    /// <summary>
    /// Creates a ChatConfiguration wrapping a separate IChatClient used by the quality
    /// evaluators (the "judge" LLM that scores the agent's responses).
    /// </summary>
    public static ChatConfiguration CreateEvaluatorChatConfiguration()
    {
        return new ChatConfiguration(CreateChatClient());
    }
}
