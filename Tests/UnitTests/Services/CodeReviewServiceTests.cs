using CodeReviewAgent.Services;
using CodeReviewAgent.Tests.TestFixtures;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace CodeReviewAgent.Tests.UnitTests.Services;

public class CodeReviewServiceTests
{
    private readonly Mock<ILogger<CodeReviewService>> _mockLogger;
    private readonly Mock<AzureDevOpsMcpClient> _mockAdoClient;
    private readonly Mock<CodebaseContextService> _mockCodebaseService;
    private readonly Mock<CodeReviewOrchestrator> _mockOrchestrator;
    private readonly CodeReviewService _codeReviewService;

    public CodeReviewServiceTests()
    {
        _mockLogger = new Mock<ILogger<CodeReviewService>>();
        _mockAdoClient = new Mock<AzureDevOpsMcpClient>();
        _mockCodebaseService = new Mock<CodebaseContextService>();
        _mockOrchestrator = new Mock<CodeReviewOrchestrator>();

        _codeReviewService = new CodeReviewService(
            _mockLogger.Object,
            _mockAdoClient.Object,
            _mockCodebaseService.Object,
            _mockOrchestrator.Object
        );
    }

    [Fact]
    public async Task ReviewPullRequestAsync_ValidPR_ReturnsSuccess()
    {
        // Arrange
        var pullRequest = MockPullRequestData.GetSamplePullRequest();
        var changedFiles = MockPullRequestData.GetSampleChangedFiles();
        
        _mockAdoClient.Setup(x => x.GetPullRequestAsync("TestProject", "test-repository", 12345))
            .ReturnsAsync(pullRequest);
        
        _mockAdoClient.Setup(x => x.GetPullRequestFilesAsync("TestProject", "test-repository", 12345))
            .ReturnsAsync(changedFiles);

        _mockOrchestrator.Setup(x => x.ReviewFilesAsync(It.IsAny<List<Models.PullRequestFile>>(), It.IsAny<string>()))
            .ReturnsAsync(new List<Models.ReviewComment>
            {
                new Models.ReviewComment
                {
                    FilePath = "/src/Authentication/JwtService.cs",
                    LineNumber = 15,
                    Comment = "Consider using a more secure method for secret key storage",
                    Severity = "Warning"
                }
            });

        // Act
        var result = await _codeReviewService.ReviewPullRequestAsync("TestProject", "test-repository", 12345);

        // Assert
        result.Should().BeTrue();
        _mockAdoClient.Verify(x => x.GetPullRequestAsync("TestProject", "test-repository", 12345), Times.Once);
        _mockAdoClient.Verify(x => x.GetPullRequestFilesAsync("TestProject", "test-repository", 12345), Times.Once);
        _mockOrchestrator.Verify(x => x.ReviewFilesAsync(It.IsAny<List<Models.PullRequestFile>>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task ReviewPullRequestAsync_NullPR_ReturnsFalse()
    {
        // Arrange
        _mockAdoClient.Setup(x => x.GetPullRequestAsync("TestProject", "test-repository", 12345))
            .ReturnsAsync((Models.PullRequestInfo?)null);

        // Act
        var result = await _codeReviewService.ReviewPullRequestAsync("TestProject", "test-repository", 12345);

        // Assert
        result.Should().BeFalse();
        _mockAdoClient.Verify(x => x.GetPullRequestAsync("TestProject", "test-repository", 12345), Times.Once);
    }

    [Fact]
    public async Task ReviewPullRequestAsync_ExceptionThrown_ReturnsFalse()
    {
        // Arrange
        _mockAdoClient.Setup(x => x.GetPullRequestAsync("TestProject", "test-repository", 12345))
            .ThrowsAsync(new Exception("ADO API error"));

        // Act
        var result = await _codeReviewService.ReviewPullRequestAsync("TestProject", "test-repository", 12345);

        // Assert
        result.Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task ReviewPullRequestAsync_InvalidProject_ReturnsFalse(string? project)
    {
        // Act
        var result = await _codeReviewService.ReviewPullRequestAsync(project!, "test-repository", 12345);

        // Assert
        result.Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task ReviewPullRequestAsync_InvalidRepository_ReturnsFalse(string? repository)
    {
        // Act
        var result = await _codeReviewService.ReviewPullRequestAsync("TestProject", repository!, 12345);

        // Assert
        result.Should().BeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ReviewPullRequestAsync_InvalidPRId_ReturnsFalse(int prId)
    {
        // Act
        var result = await _codeReviewService.ReviewPullRequestAsync("TestProject", "test-repository", prId);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task GetReviewSummaryAsync_ValidPR_ReturnsSummary()
    {
        // Arrange
        var pullRequest = MockPullRequestData.GetSamplePullRequest();
        
        _mockAdoClient.Setup(x => x.GetPullRequestAsync("TestProject", "test-repository", 12345))
            .ReturnsAsync(pullRequest);

        // Act
        var summary = await _codeReviewService.GetReviewSummaryAsync("TestProject", "test-repository", 12345);

        // Assert
        summary.Should().NotBeNullOrEmpty();
        summary.Should().Contain("Add new feature for user authentication");
        summary.Should().Contain("JWT authentication support");
    }
}