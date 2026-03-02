using System.Diagnostics;
using System.Text.Json;

namespace CodeReviewAgent.Tests;

/// <summary>
/// Simple test runner that doesn't require external testing frameworks
/// Perfect for CI/CD and automated testing without package dependencies
/// </summary>
public static class TestRunner
{
    private static int _totalTests = 0;
    private static int _passedTests = 0;
    private static int _failedTests = 0;
    private static readonly List<string> _failureDetails = new();

    public static async Task<int> Main(string[] args)
    {
        Console.WriteLine("╔══════════════════════════════════════════════════╗");
        Console.WriteLine("║           CODE REVIEW AGENT TEST SUITE          ║");
        Console.WriteLine("╚══════════════════════════════════════════════════╝");
        Console.WriteLine();

        var stopwatch = Stopwatch.StartNew();

        try
        {
            // Run all test categories
            await RunUnitTests();
            await RunIntegrationTests();
            await RunPerformanceTests();

            stopwatch.Stop();
            PrintSummary(stopwatch.Elapsed);

            return _failedTests == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Test execution failed: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
            return 1;
        }
    }

    private static async Task RunUnitTests()
    {
        Console.WriteLine("🧪 UNIT TESTS");
        Console.WriteLine("═════════════");

        // Mock AI Service Tests
        Test("MockChatClient_GeneratesResponse", () =>
        {
            var mockClient = new MockChatClient("Test response");
            var result = mockClient.CompleteAsync(new[] { 
                new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "Test prompt")
            }).Result;

            Assert.NotNull(result, "Response should not be null");
            Assert.NotEmpty(result.Message.Text ?? "", "Response should have content");
            Assert.Contains("Test response", result.Message.Text ?? "", "Response should contain expected text");
        });

        // Mock Embedding Tests
        Test("MockEmbeddingGenerator_GeneratesEmbeddings", async () =>
        {
            var mockGenerator = new MockEmbeddingGenerator();
            var result = await mockGenerator.GenerateAsync(new[] { "test input" });

            Assert.NotNull(result, "Embeddings should not be null");
            Assert.Equal(1, result.Count(), "Should generate one embedding");
            
            var embedding = result.First();
            Assert.NotNull(embedding.Vector, "Embedding vector should not be null");
            Assert.Equal(1536, embedding.Vector.Count, "Embedding should have correct dimensions");
        });

        // File Processing Tests
        Test("PullRequestFile_ValidatesCorrectly", () =>
        {
            var file = MockPullRequestData.GetSampleChangedFiles().First();
            
            Assert.NotEmpty(file.Path, "File path should not be empty");
            Assert.NotEmpty(file.Content, "File content should not be empty");
            Assert.NotEmpty(file.UnifiedDiff, "File diff should not be empty");
        });

        Console.WriteLine();
    }

    private static async Task RunIntegrationTests()
    {
        Console.WriteLine("🔗 INTEGRATION TESTS");
        Console.WriteLine("═══════════════════");

        // End-to-end workflow simulation
        Test("CodeReview_EndToEndWorkflow", async () =>
        {
            var mockChatClient = new MockChatClient();
            var pullRequest = MockPullRequestData.GetSamplePullRequest();
            var files = MockPullRequestData.GetSampleChangedFiles();

            // Simulate review process
            var startTime = DateTime.UtcNow;
            
            var reviewTasks = files.Select(async file =>
            {
                var response = await mockChatClient.CompleteAsync(new[]
                {
                    new Microsoft.Extensions.AI.ChatMessage(
                        Microsoft.Extensions.AI.ChatRole.User,
                        $"Review this {Path.GetExtension(file.Path)} file:\n{file.Content}")
                });
                return response.Message.Text ?? "";
            });

            var reviews = await Task.WhenAll(reviewTasks);
            var duration = DateTime.UtcNow - startTime;

            Assert.Equal(files.Count, reviews.Length, "Should generate reviews for all files");
            Assert.All(reviews, review => Assert.NotEmpty(review, "Each review should have content"));
            Assert.True(duration.TotalSeconds < 10, "Reviews should complete within reasonable time");
        });

        // Parallel processing test
        Test("ParallelProcessing_MultipleFiles", async () =>
        {
            var mockChatClient = new MockChatClient();
            var fileCount = 20;
            var files = Enumerable.Range(1, fileCount)
                .Select(i => $"File {i} content")
                .ToArray();

            var startTime = DateTime.UtcNow;
            
            var tasks = files.Select(async content =>
            {
                return await mockChatClient.CompleteAsync(new[]
                {
                    new Microsoft.Extensions.AI.ChatMessage(
                        Microsoft.Extensions.AI.ChatRole.User,
                        $"Review: {content}")
                });
            });

            var results = await Task.WhenAll(tasks);
            var duration = DateTime.UtcNow - startTime;

            Assert.Equal(fileCount, results.Length, "Should process all files");
            Assert.True(duration.TotalSeconds < 5, "Parallel processing should be fast");
        });

        Console.WriteLine();
    }

    private static async Task RunPerformanceTests()
    {
        Console.WriteLine("⚡ PERFORMANCE TESTS");
        Console.WriteLine("═══════════════════");

        // Large file handling
        Test("Performance_LargeFileHandling", async () =>
        {
            var mockChatClient = new MockChatClient();
            var largeContent = string.Join("\n", Enumerable.Range(1, 10000)
                .Select(i => $"Line {i}: Some code content here"));

            var stopwatch = Stopwatch.StartNew();
            
            var result = await mockChatClient.CompleteAsync(new[]
            {
                new Microsoft.Extensions.AI.ChatMessage(
                    Microsoft.Extensions.AI.ChatRole.User,
                    largeContent)
            });

            stopwatch.Stop();

            Assert.NotNull(result, "Should handle large files");
            Assert.True(stopwatch.ElapsedMilliseconds < 5000, "Should process large files within 5 seconds");
        });

        // Memory usage test
        Test("Performance_MemoryUsage", async () =>
        {
            var initialMemory = GC.GetTotalMemory(true);
            var mockChatClient = new MockChatClient();
            
            // Process multiple files to test memory usage
            for (int i = 0; i < 50; i++)
            {
                await mockChatClient.CompleteAsync(new[]
                {
                    new Microsoft.Extensions.AI.ChatMessage(
                        Microsoft.Extensions.AI.ChatRole.User,
                        $"File {i} content with some data")
                });
            }

            var finalMemory = GC.GetTotalMemory(true);
            var memoryIncrease = finalMemory - initialMemory;

            Assert.True(memoryIncrease < 50_000_000, "Memory usage should stay reasonable (< 50MB)");
        });

        Console.WriteLine();
    }

    private static void Test(string testName, Action testAction)
    {
        _totalTests++;
        
        try
        {
            Console.Write($"  {testName}... ");
            testAction();
            Console.WriteLine("✅ PASS");
            _passedTests++;
        }
        catch (Exception ex)
        {
            Console.WriteLine("❌ FAIL");
            _failedTests++;
            _failureDetails.Add($"{testName}: {ex.Message}");
        }
    }

    private static void Test(string testName, Func<Task> testAction)
    {
        _totalTests++;
        
        try
        {
            Console.Write($"  {testName}... ");
            testAction().Wait();
            Console.WriteLine("✅ PASS");
            _passedTests++;
        }
        catch (Exception ex)
        {
            Console.WriteLine("❌ FAIL");
            _failedTests++;
            var innerEx = ex.InnerException ?? ex;
            _failureDetails.Add($"{testName}: {innerEx.Message}");
        }
    }

    private static void PrintSummary(TimeSpan duration)
    {
        Console.WriteLine("╔══════════════════════════════════════════════════╗");
        Console.WriteLine("║                   TEST SUMMARY                   ║");
        Console.WriteLine("╚══════════════════════════════════════════════════╝");
        Console.WriteLine();
        Console.WriteLine($"Total Tests:    {_totalTests}");
        Console.WriteLine($"Passed:         {_passedTests} ✅");
        Console.WriteLine($"Failed:         {_failedTests} {(_failedTests > 0 ? "❌" : "")}");
        Console.WriteLine($"Duration:       {duration.TotalSeconds:F2} seconds");
        Console.WriteLine($"Success Rate:   {(_totalTests > 0 ? (_passedTests * 100.0 / _totalTests):0):F1}%");

        if (_failureDetails.Any())
        {
            Console.WriteLine();
            Console.WriteLine("FAILURE DETAILS:");
            Console.WriteLine("═══════════════");
            foreach (var failure in _failureDetails)
            {
                Console.WriteLine($"❌ {failure}");
            }
        }

        Console.WriteLine();
        if (_failedTests == 0)
        {
            Console.WriteLine("🎉 ALL TESTS PASSED! Code is ready for deployment.");
        }
        else
        {
            Console.WriteLine("🛑 SOME TESTS FAILED! Please fix issues before deploying.");
        }
    }

    // Simple assertion helpers
    public static class Assert
    {
        public static void True(bool condition, string? message = null)
        {
            if (!condition)
                throw new Exception(message ?? "Expected true but was false");
        }

        public static void False(bool condition, string? message = null)
        {
            if (condition)
                throw new Exception(message ?? "Expected false but was true");
        }

        public static void Equal<T>(T expected, T actual, string? message = null)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new Exception(message ?? $"Expected {expected} but was {actual}");
        }

        public static void NotNull<T>(T obj, string? message = null)
        {
            if (obj == null)
                throw new Exception(message ?? "Expected non-null value");
        }

        public static void NotEmpty(string str, string? message = null)
        {
            if (string.IsNullOrEmpty(str))
                throw new Exception(message ?? "Expected non-empty string");
        }

        public static void Contains(string expectedSubstring, string actualString, string? message = null)
        {
            if (!actualString.Contains(expectedSubstring))
                throw new Exception(message ?? $"Expected string to contain '{expectedSubstring}'");
        }

        public static void All<T>(IEnumerable<T> collection, Action<T> assertion)
        {
            foreach (var item in collection)
            {
                assertion(item);
            }
        }
    }
}