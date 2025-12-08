using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Azure.AI.OpenAI;
using Azure;

namespace CodeReviewAgent.Demo;

/// <summary>
/// Simple program to run the Microsoft.Agents.AI framework demos
/// </summary>
public class RunDemo
{
    public static async Task Main(string[] args)
    {
        Console.WriteLine("╔═══════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║   Microsoft.Agents.AI Framework Demo                         ║");
        Console.WriteLine("║   Demonstrating Multi-Agent Orchestration & Context Mgmt     ║");
        Console.WriteLine("╚═══════════════════════════════════════════════════════════════╝\n");

        // Setup logging
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });

        var logger = loggerFactory.CreateLogger<AgentFrameworkDemo>();

        // Get environment variables
        var azureOpenAiEndpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
        var azureOpenAiApiKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
        var azureOpenAiDeployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT") ?? "gpt-4";

        if (string.IsNullOrEmpty(azureOpenAiEndpoint) || string.IsNullOrEmpty(azureOpenAiApiKey))
        {
            Console.WriteLine("❌ Missing environment variables:");
            Console.WriteLine("   Set AZURE_OPENAI_ENDPOINT and AZURE_OPENAI_API_KEY");
            return;
        }

        Console.WriteLine($"✅ Using Azure OpenAI with deployment: {azureOpenAiDeployment}\n");

        // Create IChatClient
        var azureClient = new AzureOpenAIClient(
            new Uri(azureOpenAiEndpoint),
            new AzureKeyCredential(azureOpenAiApiKey));

        var chatClient = azureClient.GetChatClient(azureOpenAiDeployment).AsIChatClient();

        // Create and run demos
        var demo = new AgentFrameworkDemo(chatClient, logger);

        if (args.Length > 0 && int.TryParse(args[0], out int demoNumber))
        {
            // Run specific demo
            switch (demoNumber)
            {
                case 1:
                    demo.CreateSpecializedAgent();
                    break;
                case 2:
                    await demo.DemonstrateContextManagement();
                    break;
                case 3:
                    await demo.DemonstrateMultiAgentOrchestration();
                    break;
                case 4:
                    await demo.DemonstrateAgentResponse();
                    break;
                default:
                    Console.WriteLine("Invalid demo number. Use 1-4 or no argument to run all.");
                    break;
            }
        }
        else
        {
            // Run all demos
            await demo.RunAllDemos();
        }

        Console.WriteLine("\n\n✨ Demo completed! Press any key to exit...");
        Console.ReadKey();
    }
}
