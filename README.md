# Code Review Agent

A Semantic Kernel-based AI agent that automatically reviews Azure DevOps pull requests and posts intelligent code review comments.

## Features

- **Intelligent Code Analysis**: Uses GPT-4 to analyze code changes for:
  - Code quality and best practices
  - Potential bugs and security vulnerabilities
  - Performance considerations
  - Maintainability and readability

- **Azure DevOps Integration**:
  - Fetches pull request details and file changes via Azure DevOps REST API
  - Posts review comments directly to the pull request
  - Supports different comment types (suggestions, issues, nitpicks)

- **Semantic Kernel Integration**:
  - Built on Microsoft Semantic Kernel framework
  - Modular agent architecture
  - Extensible for additional AI capabilities

## Prerequisites

- .NET 9.0 or later
- OpenAI API key
- Azure DevOps organization and Personal Access Token (PAT)
- Azure DevOps project with pull requests

## Setup

1. **Clone and build the project**:
   ```bash
   git clone <repository-url>
   cd CodeReviewAgent
   dotnet build
   ```

2. **Set environment variables**:
   ```bash
   export OPENAI_API_KEY="your-openai-api-key"
   export ADO_ORGANIZATION="your-ado-organization"
   export ADO_PAT="your-personal-access-token"
   # Optional: export MCP_SERVER_URL="http://localhost:3000"
   ```

3. **Create Azure DevOps PAT**:
   - Go to Azure DevOps → User Settings → Personal Access Tokens
   - Create new token with these scopes:
     - Code (read)
     - Pull Request (read/write)
     - Project and Team (read)

## Usage

### Command Line

Review a specific pull request:
```bash
dotnet run <project-name> <pull-request-id>
```

Example:
```bash
dotnet run MyProject 123
```

### Programmatic Usage

```csharp
var codeReviewAgent = serviceProvider.GetRequiredService<CodeReviewAgentService>();

// Review a pull request
var success = await codeReviewAgent.ReviewPullRequestAsync("MyProject", 123);

// Get review summary
var summary = await codeReviewAgent.GetReviewSummaryAsync("MyProject", 123);
```

## Architecture

### System Overview

```
┌─────────────────────────────────────────────────────────────────────────┐
│                          Code Review Agent                              │
│                           (Program.cs)                                  │
└────────────┬────────────────────────────────────────────────────────────┘
             │
             ├──────────────┬──────────────────────────────────────────────┐
             │              │                                              │
        ┌────▼─────┐   ┌────▼──────┐                              ┌───────▼────────┐
        │ CLI Mode │   │ Web Mode  │                              │  Configuration │
        │          │   │           │                              │  & DI Setup    │
        └────┬─────┘   └────┬──────┘                              └───────┬────────┘
             │              │                                              │
             │         ┌────▼──────────────────────────────┐              │
             │         │   CodeReviewController.cs         │              │
             │         │  - GET /api/codereview/prs        │◄─────────────┘
             │         │  - POST /api/codereview/review    │
             │         │  - GET /api/codereview/summary    │
             │         └────┬──────────────────────────────┘
             │              │
             └──────────────┴───────────────────┐
                                                │
                         ┌──────────────────────▼─────────────────────┐
                         │   CodeReviewAgentService.cs                │
                         │   - ReviewPullRequestAsync()               │
                         │   - GetReviewSummaryAsync()                │
                         └──────────────────────┬─────────────────────┘
                                                │
                    ┌───────────────────────────┼────────────────────────┐
                    │                           │                        │
         ┌──────────▼─────────────┐  ┌──────────▼──────────┐  ┌────────▼─────────┐
         │  AzureDevOpsMcpClient  │  │ CodeReviewService   │  │ CodebaseCache    │
         │  - GetPullRequest()    │  │ - ReviewFile()      │  │ - Store/Retrieve │
         │  - GetFiles()          │  │ - ParseResponse()   │  │   PR data        │
         │  - PostComment()       │  └──────────┬──────────┘  └──────────────────┘
         └──────────┬─────────────┘             │
                    │                 ┌─────────▼──────────────────────────┐
         ┌──────────▼─────────────┐   │  CodeReviewOrchestrator.cs        │
         │ AzureDevOpsRestClient  │   │  - Routes files to language agents│
         │  - REST API calls to   │   │  - Uses Semantic Kernel function  │
         │    Azure DevOps        │   │    calling for agent selection    │
         └──────────┬─────────────┘   └────────┬──────────────────────────┘
                    │                          │
                    │              ┌───────────┼────────────┐
                    │              │           │            │
                    │   ┌──────────▼──┐  ┌─────▼──────┐  ┌─▼───────────┐
                    │   │  Python     │  │  DotNet    │  │   Rust      │
                    │   │ ReviewAgent │  │ReviewAgent │  │ ReviewAgent │
                    │   │  (.py)      │  │ (.cs)      │  │  (.rs)      │
                    │   └──────┬──────┘  └─────┬──────┘  └──┬──────────┘
                    │          │               │            │
                    │          └───────────────┴────────────┘
                    │                         │
                    │          ┌──────────────▼─────────────────────┐
                    │          │  CodebaseContextService.cs         │
                    │          │  (RAG - Retrieval Augmented Gen.)  │
                    │          │  - Embedding generation            │
                    │          │  - Semantic search                 │
                    │          │  - Context enrichment              │
                    │          └──────────────┬─────────────────────┘
                    │                         │
                    │          ┌──────────────▼─────────────────────┐
                    └──────────►  Microsoft Semantic Kernel         │
                               │  - AI orchestration                │
                               │  - Plugin system                   │
                               │  - Function calling                │
                               └──────────────┬─────────────────────┘
                                              │
                    ┌─────────────────────────┼─────────────────────┐
                    │                         │                     │
         ┌──────────▼─────────┐    ┌──────────▼──────────┐   ┌─────▼──────────┐
         │  Azure DevOps      │    │  OpenAI / Azure     │   │  Data Models   │
         │  - Pull Requests   │    │  OpenAI             │   │ - PullRequest  │
         │  - Files           │    │  - GPT-4/GPT-5      │   │ - FileChange   │
         │  - Comments        │    │  - Embeddings       │   │ - Comment      │
         └────────────────────┘    └─────────────────────┘   └────────────────┘
```

### Key Components

**Entry Points:**
- **Program.cs** - Main entry point, supports CLI and Web UI modes

**Controllers:**
- **CodeReviewController** - REST API endpoints for web interface

**Core Services:**
- **CodeReviewAgentService** - Main orchestrator for PR reviews
- **CodeReviewOrchestrator** - Routes files to language-specific agents
- **CodeReviewService** - AI-powered code analysis
- **CodebaseContextService** - RAG implementation for context enrichment

**External Integration:**
- **AzureDevOpsMcpClient** - MCP protocol wrapper for Azure DevOps
- **AzureDevOpsRestClient** - Direct REST API calls to Azure DevOps

**Language Agents** (Extensible):
- **PythonReviewAgent** - Python-specific code review
- **DotNetReviewAgent** - C#/.NET code review
- **RustReviewAgent** - Rust code review

**AI Integration:**
- **Microsoft Semantic Kernel** - AI orchestration framework
- **OpenAI/Azure OpenAI** - GPT models for code analysis and embeddings

**Supporting Services:**
- **CodebaseCache** - In-memory caching for PR data
- **Models** - Data transfer objects for PR, files, and comments

### Workflow

1. Fetch pull request details from Azure DevOps
2. Retrieve changed files and their content
3. Route each file to appropriate language-specific agent
4. Analyze files using GPT-4 with RAG-enhanced context
5. Generate structured review comments
6. Post comments back to the pull request
7. Provide comprehensive summary report

The architecture uses a **plugin-based system** where language-specific agents can be easily added, and Semantic Kernel's function calling dynamically routes files to the appropriate agent based on file extension.

## Configuration

### Environment Variables

| Variable | Required | Description |
|----------|----------|-------------|
| `OPENAI_API_KEY` | Yes | Your OpenAI API key for GPT-4 access |
| `ADO_ORGANIZATION` | Yes | Azure DevOps organization name |
| `ADO_PAT` | Yes | Personal Access Token with appropriate permissions |
| `MCP_SERVER_URL` | No | MCP server URL (defaults to http://localhost:3000) |

### Supported File Types

The agent reviews these file types:
- .cs (C#)
- .js, .ts (JavaScript/TypeScript)
- .py (Python)
- .java (Java)
- .cpp, .h (C++)
- .txt, .md (Text/Markdown)
- .yml, .yaml (YAML)
- .json, .xml (Data formats)

## Example Output

```
Code Review Summary for PR #123: Add new user authentication feature

Author: John Doe
Created: 2024-11-04 15:30
Source: feature/auth → Target: main

Files Reviewed: 5
Total Comments: 8

Issues by Severity:
- High: 1
- Medium: 3
- Low: 4

Comment Types:
- Issues: 2
- Suggestions: 4
- Nitpicks: 2
```

## Extending the Agent

### Adding New Review Rules

1. Modify the `BuildCodeReviewPrompt` method in `CodeReviewService`
2. Add new parsing logic in `ParseReviewResponse`
3. Extend the `CodeReviewComment` model as needed

### Supporting Additional Platforms

1. Create new client classes implementing similar interfaces
2. Add configuration for the new platform
3. Register services in `Program.cs`

## Troubleshooting

### Common Issues

1. **Authentication Errors**:
   - Verify PAT has correct permissions
   - Check organization name spelling

2. **API Rate Limits**:
   - Implement retry logic with exponential backoff
   - Consider using Azure OpenAI for higher limits

3. **Large Pull Requests**:
   - Agent processes files individually
   - Consider implementing batching for very large PRs

### Logging

The agent uses structured logging. Increase verbosity:
```csharp
builder.Services.AddLogging(config =>
    config.AddConsole().SetMinimumLevel(LogLevel.Debug));
```

## Contributing

1. Fork the repository
2. Create a feature branch
3. Add tests for new functionality
4. Submit a pull request

## License

MIT License - see LICENSE file for details.