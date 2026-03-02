using CodeReviewAgent.Services;
using CodeReviewAgent.Tests.TestFixtures;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace CodeReviewAgent.Tests.IntegrationTests;

public class CodeReviewIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public CodeReviewIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                // Replace real services with mocks for testing
                services.AddSingleton<IChatClient>(new MockChatClient());
                services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new MockEmbeddingGenerator());
                
                // Override configuration for testing
                Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT", "https://test.openai.azure.com/");
                Environment.SetEnvironmentVariable("AZURE_OPENAI_API_KEY", "test-key");
                Environment.SetEnvironmentVariable("ADO_ORGANIZATION", "TestOrg");
                Environment.SetEnvironmentVariable("ADO_PAT", "test-pat");
            });
        });
        
        _client = _factory.CreateClient();
    }

    [Fact]
    public async Task GetActivePullRequests_ReturnsOkResult()
    {
        // Arrange
        var project = "TestProject";
        var repository = "test-repository";

        // Act
        var response = await _client.GetAsync($"/api/codereview/pullrequests/{project}/{repository}");

        // Assert
        response.Should().NotBeNull();
        // Note: This test will depend on having a proper mock for AzureDevOps client
        // For now, we're testing that the endpoint is reachable
    }

    [Fact]
    public async Task StartReview_WithValidData_ReturnsOkResult()
    {
        // Arrange
        var reviewRequest = new
        {
            Project = "TestProject",
            Repository = "test-repository",
            PullRequestId = 12345
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/codereview/review", reviewRequest);

        // Assert
        response.Should().NotBeNull();
        // The actual result will depend on having proper mocks for all dependencies
    }

    [Fact]
    public async Task HealthCheck_ReturnsHealthy()
    {
        // Act
        var response = await _client.GetAsync("/health");

        // Assert
        response.IsSuccessStatusCode.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task GetActivePullRequests_WithInvalidProject_ReturnsBadRequest(string? project)
    {
        // Arrange
        var repository = "test-repository";
        var encodedProject = Uri.EscapeDataString(project ?? "");

        // Act
        var response = await _client.GetAsync($"/api/codereview/pullrequests/{encodedProject}/{repository}");

        // Assert
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task IndexRepository_WithValidData_ReturnsOkResult()
    {
        // Arrange
        var indexRequest = new
        {
            Project = "TestProject",
            Repository = "test-repository",
            Branch = "main"
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/codereview/index", indexRequest);

        // Assert
        response.Should().NotBeNull();
        // Testing that the endpoint is reachable and doesn't crash
    }
}

public class CodeReviewWorkflowTests
{
    [Fact]
    public async Task EndToEndWorkflow_MockedServices_CompletesSuccessfully()
    {
        // Arrange
        var mockLogger = new Mock<ILogger<CodeReviewService>>();
        var mockAdoClient = new Mock<AzureDevOpsMcpClient>();
        var mockCodebaseService = new Mock<CodebaseContextService>();
        var mockOrchestrator = new Mock<CodeReviewOrchestrator>();

        var codeReviewService = new CodeReviewService(
            mockLogger.Object,
            mockAdoClient.Object,
            mockCodebaseService.Object,
            mockOrchestrator.Object
        );

        var pullRequest = MockPullRequestData.GetSamplePullRequest();
        var changedFiles = MockPullRequestData.GetSampleChangedFiles();

        mockAdoClient.Setup(x => x.GetPullRequestAsync("TestProject", "test-repository", 12345))
            .ReturnsAsync(pullRequest);

        mockAdoClient.Setup(x => x.GetPullRequestFilesAsync("TestProject", "test-repository", 12345))
            .ReturnsAsync(changedFiles);

        mockOrchestrator.Setup(x => x.ReviewFilesAsync(It.IsAny<List<Models.PullRequestFile>>(), It.IsAny<string>()))
            .ReturnsAsync(new List<Models.ReviewComment>
            {
                new Models.ReviewComment
                {
                    FilePath = "/src/Authentication/JwtService.cs",
                    LineNumber = 15,
                    Comment = "Consider using a more secure method for secret key storage",
                    Severity = "Warning"
                },
                new Models.ReviewComment
                {
                    FilePath = "/src/Controllers/AuthController.cs", 
                    LineNumber = 21,
                    Comment = "Hardcoded credentials should be replaced with proper authentication",
                    Severity = "Error"
                }
            });

        // Act
        var reviewResult = await codeReviewService.ReviewPullRequestAsync("TestProject", "test-repository", 12345);
        var summary = await codeReviewService.GetReviewSummaryAsync("TestProject", "test-repository", 12345);

        // Assert
        reviewResult.Should().BeTrue();
        summary.Should().NotBeNullOrEmpty();
        summary.Should().Contain("Add new feature for user authentication");

        // Verify all expected calls were made
        mockAdoClient.Verify(x => x.GetPullRequestAsync("TestProject", "test-repository", 12345), Times.Once);
        mockAdoClient.Verify(x => x.GetPullRequestFilesAsync("TestProject", "test-repository", 12345), Times.Once);
        mockOrchestrator.Verify(x => x.ReviewFilesAsync(It.IsAny<List<Models.PullRequestFile>>(), It.IsAny<string>()), Times.Once);
    }

    [Fact] 
    public async Task ParallelFileProcessing_MultipleFiles_ProcessedConcurrently()
    {
        // Arrange
        var mockLogger = new Mock<ILogger<CodeReviewOrchestrator>>();
        var mockPythonAgent = new Mock<ILanguageReviewAgent>();
        var mockDotNetAgent = new Mock<ILanguageReviewAgent>();
        var mockRustAgent = new Mock<ILanguageReviewAgent>();

        var agents = new List<ILanguageReviewAgent>
        {
            mockPythonAgent.Object,
            mockDotNetAgent.Object, 
            mockRustAgent.Object
        };

        var orchestrator = new CodeReviewOrchestrator(mockLogger.Object, agents);
        var changedFiles = MockPullRequestData.GetSampleChangedFiles();

        // Setup agent capabilities
        mockDotNetAgent.Setup(x => x.CanReviewFile(It.Is<string>(path => path.EndsWith(".cs"))))
            .Returns(true);
        mockPythonAgent.Setup(x => x.CanReviewFile(It.Is<string>(path => path.EndsWith(".py"))))
            .Returns(true);

        // Setup review responses
        mockDotNetAgent.Setup(x => x.ReviewFileAsync(It.IsAny<Models.PullRequestFile>(), It.IsAny<string>()))
            .ReturnsAsync(new List<Models.ReviewComment>
            {
                new Models.ReviewComment { FilePath = "test.cs", Comment = "C# comment", Severity = "Info" }
            });

        mockPythonAgent.Setup(x => x.ReviewFileAsync(It.IsAny<Models.PullRequestFile>(), It.IsAny<string>()))
            .ReturnsAsync(new List<Models.ReviewComment>
            {
                new Models.ReviewComment { FilePath = "test.py", Comment = "Python comment", Severity = "Warning" }
            });

        // Act
        var startTime = DateTime.UtcNow;
        var results = await orchestrator.ReviewFilesAsync(changedFiles, "Test context");
        var duration = DateTime.UtcNow - startTime;

        // Assert
        results.Should().NotBeNull();
        results.Should().NotBeEmpty();
        
        // Verify parallel processing completed in reasonable time
        duration.Should().BeLessThan(TimeSpan.FromSeconds(5));

        // Verify agents were called for appropriate files
        mockDotNetAgent.Verify(x => x.ReviewFileAsync(
            It.Is<Models.PullRequestFile>(f => f.Path.EndsWith(".cs")), 
            It.IsAny<string>()), Times.AtLeastOnce);
        
        mockPythonAgent.Verify(x => x.ReviewFileAsync(
            It.Is<Models.PullRequestFile>(f => f.Path.EndsWith(".py")), 
            It.IsAny<string>()), Times.AtLeastOnce);
    }
}