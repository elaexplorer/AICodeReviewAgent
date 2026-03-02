# Code Review Agent - Testing Guide

This document explains the comprehensive testing system for the Code Review Agent.

## 🧪 Test Structure

```
Tests/
├── TestFixtures/           # Mock data and test utilities
│   ├── MockPullRequestData.cs     # Sample PR data
│   └── MockAIService.cs           # Mock AI services
├── UnitTests/             # Fast, isolated tests
│   ├── Services/          # Service layer tests
│   └── Agents/           # Language agent tests
├── IntegrationTests/      # End-to-end workflow tests
└── PerformanceTests/      # Load and performance tests
```

## 🚀 Quick Start

### Run All Tests
```bash
# Windows
.\run-tests.ps1

# Linux/macOS
./run-tests.sh
```

### Run with Coverage
```bash
# Windows
.\run-tests.ps1 -Coverage

# Linux/macOS
./run-tests.sh --coverage
```

### Run Specific Tests
```bash
# Unit tests only
.\run-tests.ps1 -Filter "Category=Unit"

# Integration tests only  
.\run-tests.ps1 -Filter "Category=Integration"

# Specific test class
.\run-tests.ps1 -Filter "RustReviewAgent"

# Watch mode (re-runs on changes)
.\run-tests.ps1 -Watch
```

## 📊 Test Categories

### Unit Tests
- **Fast** (< 100ms per test)
- **Isolated** (no external dependencies)
- **Focused** (single component testing)

**Examples:**
- `CodeReviewServiceTests` - Core review logic
- `RustReviewAgentTests` - Language-specific review
- `MockAIService` - AI service interactions

### Integration Tests  
- **Realistic** (actual component interactions)
- **End-to-end** (complete workflows)
- **API testing** (HTTP endpoints)

**Examples:**
- `CodeReviewIntegrationTests` - Full review workflow
- `CodeReviewWorkflowTests` - Multi-component interactions

### Performance Tests
- **Load testing** (multiple files, large files)
- **Memory usage** (leak detection)
- **Response times** (SLA verification)

**Examples:**
- Large file processing
- Parallel review processing
- Memory usage limits

## 🔄 Automated Testing

### On Every Update
```bash
# Automatically runs when code changes
.\test-on-update.ps1

# Quick pre-commit checks
.\test-on-update.ps1 -PreCommit

# Full test suite
.\test-on-update.ps1 -Full
```

### Continuous Integration
Tests run automatically on:
- **Push to main/develop** branches
- **Pull requests** to main
- **Multiple platforms** (Windows, Linux, macOS)

See `.github/workflows/ci.yml` for CI configuration.

## 🎯 Test Scenarios Covered

### Core Functionality
- ✅ PR fetching and parsing
- ✅ File change detection  
- ✅ Multi-language support (C#, Python, Rust)
- ✅ Review comment generation
- ✅ Error handling and validation

### AI Integration
- ✅ Mock AI responses
- ✅ Embedding generation
- ✅ Context building
- ✅ Performance under load

### Security & Quality
- ✅ Vulnerable package detection
- ✅ Input validation
- ✅ Authentication testing
- ✅ Code formatting checks

### Performance
- ✅ Large file handling (10K+ lines)
- ✅ Parallel processing (50+ files)
- ✅ Memory usage limits
- ✅ Response time requirements

## 📈 Coverage Reports

After running tests with coverage:
```bash
# View HTML report
start TestResults/CoverageReport/index.html  # Windows
open TestResults/CoverageReport/index.html   # macOS  
xdg-open TestResults/CoverageReport/index.html # Linux
```

Coverage targets:
- **Unit Tests:** > 80% line coverage
- **Integration Tests:** > 60% code path coverage  
- **Critical paths:** 100% coverage (auth, security)

## 🛠️ Writing New Tests

### Unit Test Template
```csharp
[Fact]
public async Task MethodName_Scenario_ExpectedResult()
{
    // Arrange
    var service = new ServiceUnderTest(mockDependencies);
    var input = CreateTestInput();

    // Act
    var result = await service.MethodUnderTest(input);

    // Assert
    result.Should().NotBeNull();
    result.Property.Should().Be(expectedValue);
}
```

### Integration Test Template
```csharp
[Fact] 
public async Task EndToEndScenario_ValidInput_CompletesSuccessfully()
{
    // Arrange
    var client = _factory.CreateClient();
    var request = CreateValidRequest();

    // Act
    var response = await client.PostAsJsonAsync("/api/endpoint", request);

    // Assert
    response.IsSuccessStatusCode.Should().BeTrue();
    var result = await response.Content.ReadFromJsonAsync<ResultType>();
    result.Should().NotBeNull();
}
```

## 🔍 Debugging Tests

### View Test Output
```bash
.\run-tests.ps1 -Verbose
```

### Run Single Test
```bash
dotnet test --filter "MethodName_Scenario_ExpectedResult"
```

### Debug in IDE
1. Set breakpoints in test code
2. Run test in debug mode
3. Step through execution

## 📝 Test Best Practices

### ✅ Good Practices
- **AAA Pattern:** Arrange, Act, Assert
- **Descriptive names:** `Method_Scenario_ExpectedResult`
- **Independent tests:** No shared state
- **Fast execution:** Unit tests < 100ms
- **Meaningful assertions:** Use FluentAssertions
- **Mock external dependencies:** Use Moq for isolation

### ❌ Avoid
- **Brittle tests:** Overly specific assertions
- **Slow tests:** Heavy I/O in unit tests
- **Flaky tests:** Time-dependent or random behavior
- **Shared state:** Tests affecting each other
- **Magic values:** Use constants or builders

## 🚨 Troubleshooting

### Common Issues

**Tests not running:**
```bash
# Check .NET installation
dotnet --version

# Restore packages
dotnet restore

# Build first
dotnet build
```

**Coverage not generating:**
```bash
# Install coverage tools
dotnet tool install -g dotnet-reportgenerator-globaltool
```

**Tests timing out:**
```bash
# Increase timeout for slow tests
dotnet test --logger:"console;verbosity=detailed"
```

### Getting Help
1. Check test output for specific errors
2. Review this documentation
3. Run tests with `-Verbose` flag for detailed output
4. Check CI logs for platform-specific issues

## 📊 Metrics Dashboard

Key metrics tracked:
- **Test Count:** Total number of tests
- **Coverage %:** Code coverage percentage  
- **Pass Rate:** Percentage of passing tests
- **Execution Time:** Total test suite duration
- **Flaky Tests:** Tests with inconsistent results

View metrics in CI/CD pipeline or generate locally with coverage reports.