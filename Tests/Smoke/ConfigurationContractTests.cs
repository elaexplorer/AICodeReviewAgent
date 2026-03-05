using CodeReviewAgent.Controllers;
using Xunit;

namespace CodeReviewAgent.Tests.Smoke;

public class ConfigurationContractTests
{
    [Fact]
    public void ConfigRequest_Defaults_AreUiCompatible()
    {
        var req = new ConfigRequest();

        Assert.Equal("gpt-4", req.ChatDeployment);
        Assert.Equal("2024-02-01", req.ChatApiVersion);
        Assert.Equal("text-embedding-ada-002", req.EmbeddingDeployment);
        Assert.Equal("2024-02-01", req.EmbeddingApiVersion);
    }

    [Fact]
    public void RequestModels_DefaultProject_RemainsSampleSafe()
    {
        var review = new ReviewRequest();
        var index = new IndexRequest();

        Assert.Equal("MyProject", review.Project);
        Assert.Equal("MyProject", index.Project);
        Assert.Equal("master", index.Branch);
    }
}
