using CodeReviewAgent.Tests.TestFixtures;
using FluentAssertions;
using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace CodeReviewAgent.Tests.PerformanceTests;

public class CodeReviewPerformanceTests
{
    private readonly ITestOutputHelper _output;

    public CodeReviewPerformanceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task ReviewLargeFile_CompletesWithinTimeLimit()
    {
        // Arrange
        var mockChatClient = new MockChatClient();
        var largeFile = GenerateLargeCodeFile(10000); // 10K lines

        var stopwatch = Stopwatch.StartNew();

        // Act
        var result = await mockChatClient.CompleteAsync(new[]
        {
            new Microsoft.Extensions.AI.ChatMessage(
                Microsoft.Extensions.AI.ChatRole.User, 
                $"Review this large code file:\n{largeFile}")
        });

        stopwatch.Stop();

        // Assert
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30));
        result.Should().NotBeNull();
        result.Message.Text.Should().NotBeNullOrEmpty();

        _output.WriteLine($"Large file review completed in: {stopwatch.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task ReviewMultipleFiles_ProcessedInParallel()
    {
        // Arrange
        var mockChatClient = new MockChatClient();
        var files = GenerateMultipleCodeFiles(50); // 50 files

        var stopwatch = Stopwatch.StartNew();

        // Act
        var tasks = files.Select(async file =>
        {
            return await mockChatClient.CompleteAsync(new[]
            {
                new Microsoft.Extensions.AI.ChatMessage(
                    Microsoft.Extensions.AI.ChatRole.User, 
                    $"Review this file:\n{file}")
            });
        });

        var results = await Task.WhenAll(tasks);
        stopwatch.Stop();

        // Assert
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(60));
        results.Should().HaveCount(50);
        results.Should().AllSatisfy(result => 
        {
            result.Should().NotBeNull();
            result.Message.Text.Should().NotBeNullOrEmpty();
        });

        _output.WriteLine($"Parallel processing of {files.Count} files completed in: {stopwatch.ElapsedMilliseconds}ms");
        _output.WriteLine($"Average time per file: {stopwatch.ElapsedMilliseconds / files.Count}ms");
    }

    [Theory]
    [InlineData(100)]
    [InlineData(1000)]  
    [InlineData(5000)]
    public async Task EmbeddingGeneration_ScalesWithContentSize(int lines)
    {
        // Arrange
        var mockEmbeddingGenerator = new MockEmbeddingGenerator();
        var content = GenerateLargeCodeFile(lines);

        var stopwatch = Stopwatch.StartNew();

        // Act
        var result = await mockEmbeddingGenerator.GenerateAsync(new[] { content });

        stopwatch.Stop();

        // Assert
        result.Should().NotBeNull();
        result.Should().HaveCount(1);
        
        // Performance should scale reasonably with content size
        var expectedMaxTime = TimeSpan.FromMilliseconds(lines * 0.1); // ~0.1ms per line
        stopwatch.Elapsed.Should().BeLessThan(expectedMaxTime);

        _output.WriteLine($"Embedding generation for {lines} lines completed in: {stopwatch.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task MemoryUsage_StaysWithinLimits()
    {
        // Arrange
        var initialMemory = GC.GetTotalMemory(true);
        var mockChatClient = new MockChatClient();
        var files = GenerateMultipleCodeFiles(100);

        // Act
        var tasks = files.Select(async file =>
        {
            return await mockChatClient.CompleteAsync(new[]
            {
                new Microsoft.Extensions.AI.ChatMessage(
                    Microsoft.Extensions.AI.ChatRole.User, 
                    $"Review: {file}")
            });
        });

        await Task.WhenAll(tasks);

        var finalMemory = GC.GetTotalMemory(true);
        var memoryIncrease = finalMemory - initialMemory;

        // Assert
        // Memory increase should be reasonable (less than 100MB for this test)
        memoryIncrease.Should().BeLessThan(100 * 1024 * 1024);

        _output.WriteLine($"Memory increase: {memoryIncrease / (1024 * 1024)}MB");
        _output.WriteLine($"Initial memory: {initialMemory / (1024 * 1024)}MB");
        _output.WriteLine($"Final memory: {finalMemory / (1024 * 1024)}MB");
    }

    private static string GenerateLargeCodeFile(int lines)
    {
        var content = new System.Text.StringBuilder();
        
        content.AppendLine("using System;");
        content.AppendLine("using System.Collections.Generic;");
        content.AppendLine("");
        content.AppendLine("namespace TestNamespace");
        content.AppendLine("{");
        content.AppendLine("    public class GeneratedClass");
        content.AppendLine("    {");

        for (int i = 0; i < lines - 10; i++)
        {
            content.AppendLine($"        public void Method{i}() {{ Console.WriteLine(\"Method {i}\"); }}");
        }

        content.AppendLine("    }");
        content.AppendLine("}");

        return content.ToString();
    }

    private static List<string> GenerateMultipleCodeFiles(int count)
    {
        var files = new List<string>();
        
        for (int i = 0; i < count; i++)
        {
            files.Add(GenerateCodeFile(i));
        }

        return files;
    }

    private static string GenerateCodeFile(int index)
    {
        return $@"using System;

namespace TestNamespace{index}
{{
    public class TestClass{index}
    {{
        private readonly int _value = {index};
        
        public int GetValue() => _value;
        
        public void ProcessValue()
        {{
            var result = _value * 2;
            Console.WriteLine($""Processing value {{_value}}, result: {{result}}"");
        }}
    }}
}}";
    }
}