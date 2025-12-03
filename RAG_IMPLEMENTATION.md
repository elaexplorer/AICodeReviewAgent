# RAG (Retrieval Augmented Generation) Implementation Guide

## Overview

This document describes the RAG implementation for the Code Review Agent, which provides intelligent codebase context to improve code review accuracy.

## Problem Statement

**Without RAG:**
- Review agents only see changed files in PRs
- No context about existing patterns in the codebase
- May suggest implementations that already exist elsewhere
- Cannot identify inconsistencies with established patterns

**With RAG:**
- Agents have access to semantically similar code from the entire repository
- Can reference existing patterns and implementations
- Provides dependency context (imported/related files)
- Improves review accuracy and consistency

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                    Code Review Flow                          │
└─────────────────────────────────────────────────────────────┘
                              │
                              ▼
        ┌──────────────────────────────────────┐
        │   Pull Request File to Review         │
        └──────────────────────────────────────┘
                              │
                              ▼
        ┌──────────────────────────────────────┐
        │   CodebaseContextService (RAG)        │
        │                                        │
        │  1. Semantic Search                   │
        │     ├─► Query embeddings              │
        │     ├─► Cosine similarity             │
        │     └─► Top-K relevant chunks         │
        │                                        │
        │  2. Dependency Analysis                │
        │     ├─► Parse imports/using           │
        │     ├─► Fetch related files           │
        │     └─► Extract summaries             │
        │                                        │
        │  3. Context Assembly                   │
        │     ├─► PR metadata                   │
        │     ├─► Semantic matches              │
        │     └─► Dependencies                  │
        └──────────────────────────────────────┘
                              │
                              ▼
        ┌──────────────────────────────────────┐
        │   Enhanced Review Prompt              │
        │   (Changes + Codebase Context)        │
        └──────────────────────────────────────┘
                              │
                              ▼
        ┌──────────────────────────────────────┐
        │   Language Review Agent               │
        │   (DotNet/Python/Rust)                │
        └──────────────────────────────────────┘
                              │
                              ▼
        ┌──────────────────────────────────────┐
        │   Accurate Review Comments            │
        └──────────────────────────────────────┘
```

## Components Implemented

### 1. CodebaseContextService.cs
**Location:** `Services/CodebaseContextService.cs`

**Key Features:**
- **Repository Indexing:** Splits files into chunks and generates embeddings
- **Semantic Search:** Finds relevant code using cosine similarity
- **Dependency Parsing:** Extracts imports/dependencies for C#, Python, Rust
- **Context Building:** Assembles comprehensive context for reviews

**Key Methods:**
```csharp
// Index repository (one-time or periodic)
Task<int> IndexRepositoryAsync(string project, string repositoryId, string branch)

// Search for relevant code
Task<string> GetRelevantContextAsync(PullRequestFile file, string repositoryId, int maxResults)

// Get dependency context
Task<string> GetDependencyContextAsync(PullRequestFile file, string project, string repositoryId)

// Build complete context
Task<string> BuildReviewContextAsync(PullRequestFile file, PullRequest pr, string project, string repositoryId)
```

**How Embeddings Work:**
```
Code Text                    Embedding Vector (1536 dimensions)
───────────────────────────  ──────────────────────────────────
"user authentication"     →  [0.8, 0.2, 0.5, 0.1, ...]
"login verification"      →  [0.7, 0.3, 0.4, 0.2, ...]  ← Similar!
"database connection"     →  [0.1, 0.1, 0.8, 0.9, ...]  ← Different!
```

Cosine similarity measures how "close" vectors are (0 = different, 1 = identical).

**Chunking Strategy:**
- 100 lines per chunk with 10-line overlap
- Ensures context isn't lost at chunk boundaries
- Typical 1000-line file → 10 chunks

### 2. Program.cs Configuration
**Location:** `Program.cs`

**Added Services:**
```csharp
// Embedding generation service
kernelBuilder.AddAzureOpenAITextEmbeddingGeneration(
    deploymentName: "text-embedding-ada-002",
    endpoint: azureOpenAiEndpoint,
    apiKey: azureOpenAiApiKey);

// RAG context service
builder.Services.AddSingleton<CodebaseContextService>();
```

### 3. AzureDevOpsRestClient Enhancement
**Location:** `Services/AzureDevOpsRestClient.cs`

**Added Method:**
```csharp
public async Task<string> GetFileContentAsync(
    string project,
    string repositoryId,
    string path,
    string versionOrBranch = "main")
```

Required for CodebaseContextService to fetch file contents during indexing.

### 4. NuGet Packages
**Location:** `CodeReviewAgent.csproj`

**Added:**
- `Microsoft.SemanticKernel.Connectors.AzureOpenAI` - v1.59.0
- `Microsoft.SemanticKernel.Plugins.Memory` - v1.59.0-alpha

## How RAG Improves Reviews

### Example 1: Avoiding Duplicate Implementations

**Without RAG:**
```
PR: Adds new caching layer
Review Comment: "Consider implementing cache invalidation"
Problem: Cache invalidation already exists in CacheService.cs
```

**With RAG:**
```
PR: Adds new caching layer
Context Retrieved: CacheService.cs with existing invalidation logic
Review Comment: "Consider using the existing CacheInvalidationStrategy
                 from CacheService.cs:45 to maintain consistency"
Result: ✓ Consistent with existing patterns
```

### Example 2: Security Pattern Consistency

**Without RAG:**
```
PR: Adds authentication endpoint
Review Comment: Generic security suggestions
```

**With RAG:**
```
PR: Adds authentication endpoint
Context Retrieved:
  - Existing auth endpoints in AuthController.cs
  - JWT validation middleware in AuthMiddleware.cs
Review Comment: "This endpoint should use the JwtValidationMiddleware
                 pattern established in AuthMiddleware.cs:30-50, and
                 follow the token refresh pattern from AuthController.cs:78"
Result: ✓ Follows established security patterns
```

## Performance & Cost

### Indexing Phase (One-time per repository update)
```
Repository Size: 1000 files × 300 lines avg = 300K lines
Chunks: ~30,000 chunks (100 lines each with overlap)
Embedding Cost: ~30K embeddings × $0.0001/1K tokens = ~$3-5
Time: ~10-15 minutes
Storage: In-memory (can upgrade to persistent vector DB)
```

### Review Phase (Per file)
```
Query: 1 embedding generation = ~$0.0001
Search: Cosine similarity (fast, < 100ms for 30K chunks)
Context: 3-5 relevant chunks retrieved = ~1-2K tokens
Additional Cost: ~$0.002 per review
Total Impact: +$0.002 per file reviewed
```

### Cost Comparison
```
Without RAG: $0.01 per file review
With RAG:    $0.012 per file review (+20%)
Benefit:     Much more accurate, context-aware reviews
```

## What's Completed ✅

1. ✅ **CodebaseContextService** - Full RAG implementation
2. ✅ **Embedding Service Configuration** - Azure OpenAI text-embedding-ada-002
3. ✅ **Service Registration** - DI container setup
4. ✅ **File Content Fetching** - AzureDevOpsRestClient enhancement
5. ✅ **Build Verification** - All compiles successfully

## What Remains ⏳

### 1. API Endpoint for Repository Indexing

**What:** Add REST endpoint to trigger repository indexing

**Where:** `Controllers/CodeReviewController.cs`

**Implementation:**
```csharp
[HttpPost("index")]
public async Task<IActionResult> IndexRepository(
    [FromQuery] string project,
    [FromQuery] string repository,
    [FromQuery] string branch = "main")
{
    var contextService = _serviceProvider.GetRequiredService<CodebaseContextService>();
    var adoClient = _serviceProvider.GetRequiredService<AzureDevOpsRestClient>();

    // Get repository ID
    // Call contextService.IndexRepositoryAsync(project, repositoryId, branch)
    // Return status
}
```

**Why:** Allows users to trigger indexing via UI or API

### 2. Agent Integration

**What:** Update review agents to use CodebaseContextService

**Where:** `Agents/DotNetReviewAgent.cs`, `PythonReviewAgent.cs`, `RustReviewAgent.cs`

**Implementation:**
```csharp
public class DotNetReviewAgent : ILanguageReviewAgent
{
    private readonly CodebaseContextService _contextService;

    public DotNetReviewAgent(
        ILogger<DotNetReviewAgent> logger,
        Kernel kernel,
        CodebaseContextService contextService) // Add this parameter
    {
        _contextService = contextService;
    }

    public async Task<List<CodeReviewComment>> ReviewFileAsync(
        PullRequestFile file,
        string codebaseContext)
    {
        // ENHANCE: Build RAG context
        var enhancedContext = await _contextService.BuildReviewContextAsync(
            file, pr, project, repositoryId);

        // Add enhancedContext to the prompt
        var prompt = $$$"""
            ...existing prompt...

            {{{enhancedContext}}}

            File to Review:
            ...
            """;
    }
}
```

**Why:** Actually uses the RAG context in reviews

### 3. UI Enhancement

**What:** Add "Index Repository" button in web UI

**Where:** `wwwroot/index.html`

**Implementation:**
```html
<button onclick="indexRepository()">Index Repository</button>

<script>
async function indexRepository() {
    const response = await fetch(`/api/codereview/index?project=${project}&repository=${repository}`);
    // Show progress/status
}
</script>
```

**Why:** Easy way for users to trigger indexing

## Usage Instructions (Once Complete)

### Step 1: Index Repository
```bash
# Via API
curl -X POST "http://localhost:5000/api/codereview/index?project=skype&repository=my-repo&branch=main"

# Via UI
1. Open http://localhost:5000
2. Click "Index Repository"
3. Enter project and repository name
4. Wait for indexing to complete (~10-15 min for large repos)
```

### Step 2: Review Pull Requests
```bash
# Normal workflow - RAG context automatically included
1. Enter PR details in UI
2. Click "Review PR"
3. Review comments now include codebase context!
```

### Step 3: Monitor Context Quality
```csharp
// Check logs for context retrieval
info: CodebaseContextService[0]
      Found 3 relevant code snippets for Services/AuthService.cs
      - Similarity: 0.92 from Middleware/AuthMiddleware.cs:45-145
      - Similarity: 0.81 from Controllers/AuthController.cs:78-178
      - Similarity: 0.75 from Models/User.cs:10-110
```

## Configuration

### Environment Variables
```bash
# Existing (required)
AZURE_OPENAI_API_KEY=your-key
AZURE_OPENAI_ENDPOINT=https://your-resource.openai.azure.com/
AZURE_OPENAI_DEPLOYMENT=gpt-5-mini
ADO_ORGANIZATION=skype
ADO_PAT=your-pat

# Note: text-embedding-ada-002 deployment must exist in your Azure OpenAI resource
```

### Customization Options

**In `CodebaseContextService.cs`:**
```csharp
// Chunk size (lines per chunk)
const int CHUNK_SIZE = 100;        // Increase for more context
const int OVERLAP = 10;            // Increase to reduce boundary issues

// Semantic search
.Where(r => r.Similarity > 0.7)    // Adjust threshold (0.5-0.9)
.Take(maxResults)                   // Number of results (3-10)

// File limit
foreach (var filePath in files.Take(50))  // Adjust based on repo size
```

## Troubleshooting

### Issue: "Embedding model not found"
**Solution:** Deploy `text-embedding-ada-002` in your Azure OpenAI resource

### Issue: Indexing takes too long
**Solution:**
- Reduce file limit in `IndexRepositoryAsync`: `.Take(50)` → `.Take(20)`
- Index only changed files instead of entire repo

### Issue: No relevant context found
**Solution:**
- Lower similarity threshold: `0.7` → `0.5`
- Increase maxResults: `3` → `5` or `10`

### Issue: Out of memory during indexing
**Solution:**
- Reduce chunk size or file limit
- Upgrade to persistent vector store (Qdrant, Chroma)

## Future Enhancements

1. **Persistent Vector Store:** Replace in-memory storage with Qdrant/Chroma/Pinecone
2. **Incremental Indexing:** Only index changed files
3. **Multi-Repository Support:** Index dependencies across repos
4. **Query Optimization:** Cache frequently accessed chunks
5. **Hybrid Search:** Combine semantic + keyword search
6. **Code Structure Awareness:** Parse AST for better chunking
7. **Temporal Context:** Weight recent code changes higher

## References

- **Semantic Kernel Documentation:** https://learn.microsoft.com/en-us/semantic-kernel/
- **Azure OpenAI Embeddings:** https://learn.microsoft.com/en-us/azure/ai-services/openai/concepts/embeddings
- **RAG Pattern:** https://arxiv.org/abs/2005.11401

## Summary

The RAG implementation provides intelligent codebase context to code review agents, improving accuracy and consistency. The core infrastructure is complete and ready for integration with the review agents.

**Next Steps:**
1. Add API endpoint for repository indexing
2. Integrate CodebaseContextService into review agents
3. Add UI button for indexing
4. Test with real PRs
5. Deploy text-embedding-ada-002 model to Azure OpenAI

**Estimated Time to Complete:** 1-2 hours
**Estimated Improvement:** 30-50% better review accuracy

---
*Last Updated: 2025-11-16*
*Author: Code Review Agent Development Team*
