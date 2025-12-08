# Microsoft.Agents.AI Framework Demo Guide

This guide highlights the key features of Microsoft.Agents.AI and Microsoft.Extensions.AI implemented in this code review agent.

---

## 🎯 Feature 1: Creating AI Agents with Microsoft.Agents.AI

### Location: `Agents/DotNetReviewAgent.cs` (Lines 20-66)

```csharp
public DotNetReviewAgent(
    ILogger<DotNetReviewAgent> logger,
    IChatClient chatClient)
{
    _logger = logger;

    // ✨ CREATE AN AI AGENT using ChatClientAgent
    _agent = new ChatClientAgent(
        chatClient,                    // Built on IChatClient (Microsoft.Extensions.AI)
        instructions: """               // Agent's system instructions
            You are an expert C#/.NET code reviewer with deep knowledge of:
            - C# best practices and coding standards
            - .NET Framework/.NET Core/.NET 5+ features and patterns
            - SOLID principles and design patterns
            - Common .NET security vulnerabilities (OWASP)
            ...
            """,
        name: "DotNetReviewAgent");    // Agent identifier
}
```

**Key Features:**
- `ChatClientAgent` wraps `IChatClient` to create specialized AI agents
- **System Instructions**: Define agent's personality, expertise, and behavior
- **Agent Name**: Identifier for logging, debugging, and orchestration
- **Stateful Agent**: Agent maintains its configuration across invocations

**Similar Implementation:**
- `Agents/PythonReviewAgent.cs` (Lines 20-63)
- `Agents/RustReviewAgent.cs` (Lines 20-66)

---

## 🎯 Feature 2: RAG (Retrieval Augmented Generation) with Embeddings

### Location: `Services/CodebaseContextService.cs`

### 2A. Embedding Generation (Lines 14, 70-71, 121-122)

```csharp
public class CodebaseContextService
{
    // ✨ EMBEDDING GENERATOR from Microsoft.Extensions.AI
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;

    // Generate embeddings for code chunks during indexing
    public async Task<int> IndexRepositoryAsync(...)
    {
        foreach (var chunk in fileChunks)
        {
            // ✨ GENERATE EMBEDDING using IEmbeddingGenerator
            var embeddingResponse = await _embeddingGenerator.GenerateAsync(chunk.Content);
            chunk.Embedding = embeddingResponse.Vector.ToArray();
            chunks.Add(chunk);
        }
    }

    // Generate embeddings for search queries
    public async Task<string> GetRelevantContextAsync(...)
    {
        // ✨ GENERATE QUERY EMBEDDING
        var queryEmbeddingResponse = await _embeddingGenerator.GenerateAsync(searchQuery);
        var queryVector = queryEmbeddingResponse.Vector.ToArray();

        // Semantic search using cosine similarity...
    }
}
```

### 2B. Semantic Search with Cosine Similarity (Lines 124-135)

```csharp
// ✨ SEMANTIC SEARCH - Find similar code using embeddings
var results = chunks
    .Select(chunk => new
    {
        Chunk = chunk,
        Similarity = CosineSimilarity(queryVector, chunk.Embedding)  // Vector similarity
    })
    .Where(r => r.Similarity > 0.7)  // Relevance threshold (70%)
    .OrderByDescending(r => r.Similarity)
    .Take(maxResults)
    .ToList();
```

### 2C. Cosine Similarity Implementation (Lines 375-398)

```csharp
// ✨ COSINE SIMILARITY - Calculate vector similarity
private double CosineSimilarity(float[] vectorA, float[] vectorB)
{
    double dotProduct = 0;
    double magnitudeA = 0;
    double magnitudeB = 0;

    for (int i = 0; i < vectorA.Length; i++)
    {
        dotProduct += vectorA[i] * vectorB[i];
        magnitudeA += vectorA[i] * vectorA[i];
        magnitudeB += vectorB[i] * vectorB[i];
    }

    magnitudeA = Math.Sqrt(magnitudeA);
    magnitudeB = Math.Sqrt(magnitudeB);

    return dotProduct / (magnitudeA * magnitudeB);
}
```

### 2D. Embedding Generator Registration (Program.cs:94-104)

```csharp
// ✨ REGISTER EMBEDDING GENERATOR for RAG
builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(provider =>
{
    var azureClient = new AzureOpenAIClient(
        new Uri(azureOpenAiEndpoint!),
        new AzureKeyCredential(azureOpenAiApiKey!));

    // Get EmbeddingClient and convert to IEmbeddingGenerator
    var embeddingClient = azureClient.GetEmbeddingClient("text-embedding-ada-002");
    return embeddingClient.AsIEmbeddingGenerator();
});
```

**RAG Pipeline:**
1. **Index Phase**: Generate embeddings for all code chunks → Store in vector database
2. **Query Phase**: Generate embedding for search query → Find similar vectors
3. **Retrieval Phase**: Retrieve most relevant code chunks based on similarity
4. **Augmentation Phase**: Inject retrieved context into agent's prompt

---

## 🎯 Feature 3: Multi-Agent Orchestration

### Location: `Services/CodeReviewOrchestrator.cs` (Lines 47-87)

```csharp
public async Task<List<CodeReviewComment>> ReviewFilesAsync(
    List<PullRequestFile> files,
    string codebaseContext)
{
    // ✨ MULTI-AGENT ORCHESTRATION - Process files in parallel
    var reviewTasks = files.Select(async file =>
    {
        var extension = Path.GetExtension(file.Path);

        // ✨ INTELLIGENT ROUTING - Route to specialized agent
        if (_extensionToLanguage.TryGetValue(extension, out var language) &&
            _agents.TryGetValue(language, out var agent))
        {
            _logger.LogInformation("Routing {FilePath} to {Language} agent",
                file.Path, language);

            // ✨ INVOKE SPECIALIZED AGENT
            return await agent.ReviewFileAsync(file, codebaseContext);
        }
        else
        {
            // Fallback to general agent
            return await ReviewWithGeneralAgentAsync(file, codebaseContext);
        }
    });

    // ✨ PARALLEL EXECUTION - Run all agents concurrently
    var results = await Task.WhenAll(reviewTasks);

    // ✨ RESULT AGGREGATION - Combine results from all agents
    return results.SelectMany(comments => comments).ToList();
}
```

**Orchestration Features:**
- **3 Specialized Agents**: DotNetReviewAgent, PythonReviewAgent, RustReviewAgent
- **Intelligent Routing**: Files automatically routed to appropriate agent by extension
- **Parallel Execution**: `Task.WhenAll` runs multiple agents concurrently
- **Result Aggregation**: Combines and flattens results from all agents
- **Fallback Strategy**: General agent handles unknown file types

### Agent Registration (Program.cs:148-156)

```csharp
// ✨ REGISTER LANGUAGE-SPECIFIC AGENTS
builder.Services.AddSingleton<ILanguageReviewAgent, PythonReviewAgent>();
builder.Services.AddSingleton<ILanguageReviewAgent, DotNetReviewAgent>();
builder.Services.AddSingleton<ILanguageReviewAgent, RustReviewAgent>();

// ✨ REGISTER ORCHESTRATOR - Discovers and coordinates all agents
builder.Services.AddSingleton<CodeReviewOrchestrator>();
```

---

## 🎯 Feature 4: Agent Execution with Context

### Location: `Agents/DotNetReviewAgent.cs` (Lines 68-127)

```csharp
public async Task<List<CodeReviewComment>> ReviewFileAsync(
    PullRequestFile file,
    string codebaseContext)  // ✨ RAG-provided context
{
    // Build comprehensive prompt with file changes and codebase context
    var prompt = $$$"""
        Review ONLY THE CHANGES in the following C#/.NET file from a pull request.

        File Path: {{{file.Path}}}
        Change Type: {{{file.ChangeType}}}

        ========================================
        CHANGES TO REVIEW (lines with '+' prefix):
        ========================================
        ```diff
        {{{file.UnifiedDiff}}}
        ```

        Codebase Context:
        {{{codebaseContext}}}  // ✨ RAG context injected here

        Provide a thorough code review focusing on:
        1. **Security Issues**: SQL injection, XSS, CSRF, ...
        2. **Bugs**: Null reference exceptions, race conditions, ...
        3. **Performance**: Boxing/unboxing, string concatenation, ...
        """;

    // ✨ EXECUTE AGENT with RunAsync
    var response = await _agent.RunAsync(prompt);
    var responseText = response.Text;

    // Parse JSON response into structured comments
    var comments = ParseReviewComments(responseText, file.Path);

    return comments;
}
```

**Context Flow:**
1. `CodebaseContextService` uses RAG to find relevant code snippets
2. Context injected into agent's prompt as `codebaseContext`
3. Agent reviews changes with full codebase awareness
4. Agent returns structured feedback

---

## 🎯 Feature 5: Comprehensive Context Building

### Location: `Services/CodebaseContextService.cs` (Lines 221-253)

```csharp
public async Task<string> BuildReviewContextAsync(
    PullRequestFile file,
    PullRequest pr,
    string project,
    string repositoryId)
{
    var context = new StringBuilder();

    // ✨ 1. PR-LEVEL CONTEXT
    context.AppendLine($"# Pull Request Context");
    context.AppendLine($"**Title:** {pr.Title}");
    context.AppendLine($"**Description:** {pr.Description}");

    // ✨ 2. SEMANTIC CONTEXT - Similar code via RAG
    var semanticContext = await GetRelevantContextAsync(file, repositoryId, maxResults: 3);
    if (!string.IsNullOrEmpty(semanticContext))
    {
        context.AppendLine(semanticContext);
    }

    // ✨ 3. DEPENDENCY CONTEXT - Related files via imports
    var depContext = await GetDependencyContextAsync(file, project, repositoryId);
    if (!string.IsNullOrEmpty(depContext))
    {
        context.AppendLine(depContext);
    }

    return context.ToString();
}
```

**Three Layers of Context:**
1. **PR Context**: Pull request title, description, and metadata
2. **Semantic Context**: Similar code found via RAG embeddings
3. **Dependency Context**: Related files based on imports/using statements

---

## 🎯 Feature 6: Code Chunking Strategy

### Location: `Services/CodebaseContextService.cs` (Lines 271-296)

```csharp
private List<CodeChunk> SplitIntoChunks(string content, string filePath)
{
    var chunks = new List<CodeChunk>();
    var lines = content.Split('\n');
    const int CHUNK_SIZE = 100;    // ✨ Lines per chunk
    const int OVERLAP = 10;        // ✨ Overlap between chunks

    for (int i = 0; i < lines.Length; i += (CHUNK_SIZE - OVERLAP))
    {
        var chunkLines = lines.Skip(i).Take(CHUNK_SIZE).ToArray();

        chunks.Add(new CodeChunk
        {
            Content = string.Join('\n', chunkLines),
            ChunkIndex = chunks.Count,
            StartLine = i + 1,
            EndLine = i + chunkLines.Length,
            Metadata = $"{filePath}:L{i + 1}-L{i + chunkLines.Length}",
            FilePath = filePath,
            Embedding = Array.Empty<float>()  // Filled during indexing
        });
    }

    return chunks;
}
```

**Chunking Strategy:**
- **100 lines per chunk**: Maintains context while staying within token limits
- **10 line overlap**: Ensures continuity between chunks
- **Metadata tracking**: Each chunk knows its location (file:line range)
- **Ready for embedding**: Chunks are pre-structured for embedding generation

---

## 📊 Architecture Diagram

```
┌─────────────────────────────────────────────────────────────────┐
│                    Microsoft.Extensions.AI                      │
│                         (Foundation)                            │
│  ┌──────────────────┐              ┌───────────────────────┐   │
│  │   IChatClient    │              │ IEmbeddingGenerator   │   │
│  └────────┬─────────┘              └──────────┬────────────┘   │
└───────────┼────────────────────────────────────┼────────────────┘
            │                                    │
            │                                    │
┌───────────▼────────────────────────────────────▼────────────────┐
│                    Microsoft.Agents.AI                          │
│                      (Agent Layer)                              │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │            ChatClientAgent (wraps IChatClient)          │   │
│  │  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐  │   │
│  │  │ DotNetAgent  │  │ PythonAgent  │  │  RustAgent   │  │   │
│  │  └──────────────┘  └──────────────┘  └──────────────┘  │   │
│  └─────────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────────┘
                            │
                            │
┌───────────────────────────▼─────────────────────────────────────┐
│              CodeReviewOrchestrator                             │
│         (Multi-Agent Coordination Layer)                        │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │  • Intelligent routing by file type                     │   │
│  │  • Parallel agent execution                             │   │
│  │  • Result aggregation                                   │   │
│  └─────────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────────┘
                            │
                            │
┌───────────────────────────▼─────────────────────────────────────┐
│              CodebaseContextService                             │
│              (RAG Implementation)                               │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │  1. Generate embeddings (IEmbeddingGenerator)           │   │
│  │  2. Store in vector database (in-memory)                │   │
│  │  3. Semantic search (cosine similarity)                 │   │
│  │  4. Retrieve relevant context                           │   │
│  └─────────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────────┘
```

---

## 🚀 Demo Flow

### Step 1: Index Repository (RAG Setup)
```csharp
// Index entire repository for semantic search
await codebaseContextService.IndexRepositoryAsync(project, repoId, "main");
// Result: 500+ code chunks embedded and stored
```

### Step 2: Build Context for Review
```csharp
// Get relevant context using RAG
var context = await codebaseContextService.BuildReviewContextAsync(
    file, pr, project, repoId);
// Result: Similar code + dependencies + PR metadata
```

### Step 3: Multi-Agent Review
```csharp
// Orchestrator routes to specialized agents
var comments = await orchestrator.ReviewFilesAsync(files, context);
// Result: Python files → PythonAgent, C# files → DotNetAgent, etc.
```

### Step 4: Agent Execution
```csharp
// Each agent reviews with full context
var response = await agent.RunAsync(prompt + context);
// Result: Structured review comments with severity and type
```

---

## 🎬 Key Talking Points for Demo

1. **Microsoft.Extensions.AI Foundation**
   - "We use IChatClient as the abstraction layer for AI services"
   - "This enables swapping between OpenAI, Azure OpenAI, or any other provider"

2. **Microsoft.Agents.AI Specialization**
   - "ChatClientAgent wraps IChatClient to create specialized AI agents"
   - "Each agent has its own instructions and expertise"

3. **Multi-Agent Orchestration**
   - "We have 3 specialized agents - one for each programming language"
   - "The orchestrator intelligently routes files to the right agent"
   - "All agents run in parallel for maximum performance"

4. **RAG Implementation**
   - "We use embeddings to create a semantic understanding of the codebase"
   - "When reviewing a file, we retrieve similar code for context"
   - "This gives agents awareness of patterns and conventions in the codebase"

5. **Complete Pipeline**
   - "Index → Retrieve → Augment → Generate"
   - "The agent reviews code with full context awareness"

---

## 📝 Code Locations Quick Reference

| Feature | File | Lines |
|---------|------|-------|
| **Agent Creation** | `Agents/DotNetReviewAgent.cs` | 27-65 |
| **Agent Execution** | `Agents/DotNetReviewAgent.cs` | 117-119 |
| **RAG - Embedding Generation** | `Services/CodebaseContextService.cs` | 70-71, 121-122 |
| **RAG - Semantic Search** | `Services/CodebaseContextService.cs` | 124-135 |
| **RAG - Cosine Similarity** | `Services/CodebaseContextService.cs` | 375-398 |
| **RAG - Context Building** | `Services/CodebaseContextService.cs` | 221-253 |
| **Multi-Agent Orchestration** | `Services/CodeReviewOrchestrator.cs` | 47-87 |
| **Agent Registration** | `Program.cs` | 148-156 |
| **Embedding Registration** | `Program.cs` | 94-104 |

---

## 🎯 Demo Script

### Opening
"Today I'll show you how we built a multi-agent code review system using Microsoft.Agents.AI framework combined with RAG for intelligent context awareness."

### Part 1: Agent Creation (2 min)
- Show `DotNetReviewAgent.cs` constructor
- Explain ChatClientAgent wrapping IChatClient
- Highlight specialized instructions

### Part 2: RAG Implementation (3 min)
- Show embedding generation in `CodebaseContextService.cs`
- Demonstrate semantic search with cosine similarity
- Explain the three layers of context (PR, semantic, dependencies)

### Part 3: Multi-Agent Orchestration (3 min)
- Show `CodeReviewOrchestrator.cs` routing logic
- Explain parallel execution
- Demonstrate how 3 agents work together

### Part 4: Full Pipeline (2 min)
- Show end-to-end flow from file submission to review
- Highlight how RAG context flows into agent prompts
- Show structured output

### Closing
"This demonstrates the power of Microsoft.Agents.AI for multi-agent scenarios combined with RAG for enhanced context awareness."

---

## 🔥 Best Practices Demonstrated

1. ✅ **Separation of Concerns**: Agents, orchestrator, and RAG service are decoupled
2. ✅ **Dependency Injection**: All components registered and injected via DI
3. ✅ **Parallel Execution**: Multi-agent coordination with `Task.WhenAll`
4. ✅ **Semantic Search**: Vector embeddings for intelligent code retrieval
5. ✅ **Chunking Strategy**: 100 lines with 10 line overlap for optimal context
6. ✅ **Structured Output**: JSON responses parsed into typed models
7. ✅ **Logging**: Comprehensive logging at all layers
8. ✅ **Error Handling**: Try-catch blocks with fallback strategies

---

*Generated for Microsoft.Agents.AI Framework Demonstration*
