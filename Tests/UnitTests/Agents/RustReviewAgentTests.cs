using CodeReviewAgent.Agents;
using CodeReviewAgent.Models;
using CodeReviewAgent.Tests.TestFixtures;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace CodeReviewAgent.Tests.UnitTests.Agents;

public class RustReviewAgentTests
{
    private readonly Mock<ILogger<RustReviewAgent>> _mockLogger;
    private readonly MockChatClient _mockChatClient;
    private readonly RustReviewAgent _rustReviewAgent;

    public RustReviewAgentTests()
    {
        _mockLogger = new Mock<ILogger<RustReviewAgent>>();
        _mockChatClient = new MockChatClient(
            "## Rust Code Review\n\n**Security Issue:** Potential buffer overflow in line 42\n**Performance:** Consider using Vec::with_capacity for better allocation\n**Style:** Use idiomatic Rust naming conventions"
        );

        _rustReviewAgent = new RustReviewAgent(_mockChatClient, _mockLogger.Object);
    }

    [Fact]
    public async Task ReviewFileAsync_RustFile_ReturnsComments()
    {
        // Arrange
        var rustFile = new PullRequestFile
        {
            Path = "/src/main.rs",
            ChangeType = "edit",
            Content = GetSampleRustCode(),
            UnifiedDiff = GetSampleRustDiff(),
            PreviousContent = "// Previous version"
        };

        // Act
        var result = await _rustReviewAgent.ReviewFileAsync(rustFile, "Sample codebase context");

        // Assert
        result.Should().NotBeNull();
        result.Should().NotBeEmpty();
        result.Should().AllSatisfy(comment => 
        {
            comment.FilePath.Should().Be("/src/main.rs");
            comment.Comment.Should().NotBeNullOrEmpty();
        });
    }

    [Fact]
    public async Task ReviewFileAsync_EmptyFile_ReturnsEmptyComments()
    {
        // Arrange
        var emptyFile = new PullRequestFile
        {
            Path = "/src/empty.rs",
            ChangeType = "add",
            Content = "",
            UnifiedDiff = "",
            PreviousContent = ""
        };

        // Act
        var result = await _rustReviewAgent.ReviewFileAsync(emptyFile, "");

        // Assert
        result.Should().NotBeNull();
        result.Should().BeEmpty();
    }

    [Theory]
    [InlineData("/src/lib.rs")]
    [InlineData("/tests/integration_test.rs")]
    [InlineData("/benches/benchmark.rs")]
    [InlineData("/examples/example.rs")]
    public async Task ReviewFileAsync_VariousRustFiles_ProcessesCorrectly(string filePath)
    {
        // Arrange
        var rustFile = new PullRequestFile
        {
            Path = filePath,
            ChangeType = "edit",
            Content = GetSampleRustCode(),
            UnifiedDiff = GetSampleRustDiff(),
            PreviousContent = "// Previous content"
        };

        // Act
        var result = await _rustReviewAgent.ReviewFileAsync(rustFile, "Context for " + filePath);

        // Assert
        result.Should().NotBeNull();
        if (!string.IsNullOrEmpty(rustFile.Content))
        {
            result.Should().NotBeEmpty();
        }
    }

    [Fact]
    public void SupportedFileTypes_ReturnsRustExtensions()
    {
        // Act
        var supportedTypes = _rustReviewAgent.SupportedFileTypes;

        // Assert
        supportedTypes.Should().Contain(".rs");
        supportedTypes.Should().Contain("Cargo.toml");
        supportedTypes.Should().Contain("Cargo.lock");
    }

    [Theory]
    [InlineData("/src/main.rs", true)]
    [InlineData("/tests/test.rs", true)]
    [InlineData("/Cargo.toml", true)]
    [InlineData("/Cargo.lock", true)]
    [InlineData("/src/main.py", false)]
    [InlineData("/src/main.cs", false)]
    [InlineData("/package.json", false)]
    public void CanReviewFile_VariousFiles_ReturnsCorrectResult(string filePath, bool expectedResult)
    {
        // Act
        var result = _rustReviewAgent.CanReviewFile(filePath);

        // Assert
        result.Should().Be(expectedResult);
    }

    private static string GetSampleRustCode()
    {
        return @"use std::collections::HashMap;
use serde::{Deserialize, Serialize};

#[derive(Debug, Serialize, Deserialize)]
pub struct User {
    pub id: u64,
    pub name: String,
    pub email: String,
}

impl User {
    pub fn new(id: u64, name: String, email: String) -> Self {
        Self { id, name, email }
    }
    
    pub fn validate_email(&self) -> bool {
        self.email.contains('@') && self.email.contains('.')
    }
}

pub fn create_user_map(users: Vec<User>) -> HashMap<u64, User> {
    let mut map = HashMap::new();
    
    for user in users {
        map.insert(user.id, user);
    }
    
    map
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_user_creation() {
        let user = User::new(1, ""John Doe"".to_string(), ""john@example.com"".to_string());
        assert_eq!(user.id, 1);
        assert_eq!(user.name, ""John Doe"");
        assert!(user.validate_email());
    }
    
    #[test]
    fn test_create_user_map() {
        let users = vec![
            User::new(1, ""Alice"".to_string(), ""alice@example.com"".to_string()),
            User::new(2, ""Bob"".to_string(), ""bob@example.com"".to_string()),
        ];
        
        let map = create_user_map(users);
        assert_eq!(map.len(), 2);
        assert!(map.contains_key(&1));
        assert!(map.contains_key(&2));
    }
}";
    }

    private static string GetSampleRustDiff()
    {
        return @"@@ -20,7 +20,10 @@ impl User {
 
 pub fn create_user_map(users: Vec<User>) -> HashMap<u64, User> {
-    let mut map = HashMap::new();
+    let mut map = HashMap::with_capacity(users.len());
     
     for user in users {
+        if map.contains_key(&user.id) {
+            eprintln!(""Duplicate user ID: {}"", user.id);
+        }
         map.insert(user.id, user);
     }";
    }
}