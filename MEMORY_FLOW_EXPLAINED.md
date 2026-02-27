# 🧠 Complete Guide: How Codebase Chunks Work in Memory

*Understanding the journey from code files to searchable vectors*

## 🎯 Quick Overview

The Code Review AI Agent transforms your entire codebase into **searchable semantic vectors stored in memory**. Here's how it works:

```
Repository Files → Chunks → Embeddings → In-Memory Store → Semantic Search
      ↓              ↓         ↓            ↓               ↓
   Program.cs    100-line   3072-dim    Dictionary     Find similar
   Service.cs    pieces     vectors    <string, List>   code instantly
```

## 🔄 The Complete Flow

### Step 1: Repository Indexing (`IndexRepositoryAsync`)

**Location**: `Services/CodebaseContextService.cs:80-222`

**What happens:**
```csharp
// 1. Fetch all files from Azure DevOps
var files = await _adoClient.GetRepositoryItemsAsync(project, repositoryId, branch);

// 2. Process each file (limit 50 for cost control)
foreach (var filePath in files.Take(50))
{
    // 3. Get file content
    var content = await _adoClient.GetFileContentAsync(project, repositoryId, filePath, branch);
    
    // 4. Split into chunks
    var fileChunks = SplitIntoChunks(content, filePath);
    
    // 5. Generate embedding for each chunk
    foreach (var chunk in fileChunks)
    {
        var embeddingResponse = await _embeddingGenerator.GenerateAsync(chunk.Content);
        chunk.Embedding = embeddingResponse.Vector.ToArray(); // 3072 floats
        chunks.Add(chunk);
    }
}

// 6. Store in memory
_inMemoryStore[repositoryId] = chunks;
```

**Output**: Repository indexed with chunks in memory

### Step 2: Chunking Strategy (`SplitIntoChunks`)

**Location**: `Services/CodebaseContextService.cs:516-541`

**Algorithm:**
```csharp
const int CHUNK_SIZE = 100; // lines per chunk
const int OVERLAP = 10;     // overlap between chunks

for (int i = 0; i < lines.Length; i += (CHUNK_SIZE - OVERLAP))
{
    var chunkLines = lines.Skip(i).Take(CHUNK_SIZE).ToArray();
    
    chunks.Add(new CodeChunk {
        Content = string.Join('\n', chunkLines),
        ChunkIndex = chunks.Count,
        StartLine = i + 1,
        EndLine = i + chunkLines.Length,
        Metadata = $"{filePath}:L{i + 1}-L{i + chunkLines.Length}",
        FilePath = filePath,
        Embedding = Array.Empty<float>() // Filled during indexing
    });
}
```

**Result**: A 500-line file becomes ~6 overlapping chunks

### Step 3: Memory Storage Structure

**Location**: `Services/CodebaseContextService.cs:17-28`

**Data Structure:**
```csharp
private readonly Dictionary<string, List<CodeChunk>> _inMemoryStore;

public class CodeChunk
{
    public string Content { get; set; }      // The actual code (100 lines)
    public int ChunkIndex { get; set; }      // Index within file
    public int StartLine { get; set; }       // Line 1, 91, 181, etc.
    public int EndLine { get; set; }         // Line 100, 190, 280, etc.
    public string Metadata { get; set; }     // "Program.cs:L1-L100"
    public string FilePath { get; set; }     // "/src/Program.cs"
    public float[] Embedding { get; set; }   // 3072 dimensions
}
```

**Memory Layout:**
```
_inMemoryStore = {
    "CodReviewAIAgent": [
        {
            Content: "using System;\nusing Microsoft.Extensions...",
            StartLine: 1,
            EndLine: 100,
            FilePath: "/Program.cs",
            Embedding: [0.234, -0.156, 0.891, ..., 0.345] // 3072 floats
        },
        {
            Content: "builder.Services.AddSingleton<CodebaseCache>();\n...",
            StartLine: 91,  // 10 lines overlap
            EndLine: 190,
            FilePath: "/Program.cs", 
            Embedding: [0.145, -0.267, 0.723, ..., 0.892] // 3072 floats
        },
        // ... more chunks for Program.cs
        // ... chunks for other files
    ]
}
```

### Step 4: Semantic Search (`GetRelevantContextAsync`)

**Location**: `Services/CodebaseContextService.cs:227-366`

**Search Process:**
```csharp
// 1. Build search query from PR changes
var searchQuery = BuildSearchQuery(file); // Extract added lines from diff

// 2. Generate embedding for search query  
var queryEmbeddingResponse = await _embeddingGenerator.GenerateAsync(searchQuery);
var queryVector = queryEmbeddingResponse.Vector.ToArray(); // 3072 floats

// 3. Calculate similarity against ALL chunks in memory
var chunks = _inMemoryStore[repositoryId]; // Get all chunks
var allSimilarities = chunks
    .Select(chunk => new {
        Chunk = chunk,
        Similarity = CosineSimilarity(queryVector, chunk.Embedding) // 0.0 to 1.0
    })
    .OrderByDescending(r => r.Similarity)
    .ToList();

// 4. Filter by threshold and return top matches
var results = allSimilarities
    .Where(r => r.Similarity > 0.7) // Relevance threshold
    .Take(maxResults)
    .ToList();
```

**Cosine Similarity Algorithm:**
```csharp
private double CosineSimilarity(float[] vectorA, float[] vectorB)
{
    double dotProduct = 0;
    double magnitudeA = 0;
    double magnitudeB = 0;

    for (int i = 0; i < vectorA.Length; i++) // 3072 iterations
    {
        dotProduct += vectorA[i] * vectorB[i];
        magnitudeA += vectorA[i] * vectorA[i];
        magnitudeB += vectorB[i] * vectorB[i];
    }

    return dotProduct / (Math.Sqrt(magnitudeA) * Math.Sqrt(magnitudeB));
}
```

## 📊 Memory Characteristics

### Storage Details
- **Type**: `Dictionary<string, List<CodeChunk>>`
- **Key**: Repository ID (e.g., "CodReviewAIAgent")
- **Value**: List of all chunks from that repository
- **Lifetime**: Until application restart (no persistence)
- **Thread Safety**: Not explicitly thread-safe

### Memory Usage Estimation
```
For 50 files, ~500 chunks:
- Vectors: 500 chunks × 3072 floats × 4 bytes = ~6 MB
- Text content: 500 chunks × ~2KB = ~1 MB  
- Metadata: 500 chunks × ~100 bytes = ~50 KB
- Total: ~7-8 MB per repository
```

### Performance Characteristics
- **Indexing**: O(n) files × O(m) chunks × O(API call)
- **Search**: O(k) chunks × O(3072) dimensions = O(k)
- **Typical search time**: ~10-50ms for 500 chunks

## 🔍 How to Inspect Memory Contents

### Method 1: Built-in Logging
The service logs detailed information during indexing and search:

```bash
# Enable detailed logging and index a repository
dotnet run SCC CodReviewAIAgent 123
```

**Look for these log entries:**
```
╔════════════════════════════════════════════════════════════╗
║ RAG INDEXING: Starting Repository Indexing                ║
╚════════════════════════════════════════════════════════════╝

📊 Indexing Statistics:
  Code chunks created: 487
  Embeddings generated: 487  
  Vector dimension: 3072

📦 STORAGE DETAILS:
  Storage type: In-Memory Dictionary<string, List<CodeChunk>>
  Storage key: Repository ID = 'CodReviewAIAgent'
  Total repositories in store: 1
  Memory estimate: ~5.8 MB (embeddings only)

📐 SAMPLE EMBEDDING VECTOR (first chunk):
  File: /Program.cs
  Vector dimension: 3072
  First 10 values: [0.234567, -0.156789, 0.891234, ...]
```

### Method 2: Memory Inspector Tool (In Development)
```bash
# Inspect current memory state
dotnet run -- --inspect-memory

# Specify repository to inspect
dotnet run -- --inspect-memory --repo CodReviewAIAgent --project SCC
```

### Method 3: Debug via Context Service
```csharp
var contextService = app.Services.GetRequiredService<CodebaseContextService>();

// Check if repository is indexed
bool isIndexed = contextService.IsRepositoryIndexed("CodReviewAIAgent");

// Get chunk count
int chunkCount = contextService.GetChunkCount("CodReviewAIAgent");

// Get detailed summary
string summary = contextService.GetIndexSummary("CodReviewAIAgent");
```

## 🎯 Real Example: PR Review Flow

### Scenario: Reviewing changes to `AuthService.cs`

1. **PR opened** → Code Review Agent triggered
2. **Repository indexed** (if not already):
   ```
   Files processed: 47
   Chunks created: 423  
   Memory used: ~5.2 MB
   ```

3. **PR file analyzed**:
   ```csharp
   // File: AuthService.cs
   // Changes: Added JWT token validation
   +    public bool ValidateJwtToken(string token)
   +    {
   +        var tokenHandler = new JwtSecurityTokenHandler();
   +        // ... validation logic
   +    }
   ```

4. **Search query built**:
   ```
   Query: "ValidateJwtToken JWT token validation security tokenHandler"
   ```

5. **Semantic search performed**:
   ```
   Query embedding generated: 3072 dimensions
   Searching against: 423 chunks
   Similarities calculated: 423 cosine similarity calculations
   Results above 0.7 threshold: 3 chunks found
   ```

6. **Top matches returned**:
   ```
   Match 1: SecurityService.cs:L45-L144 (similarity: 0.87)
   - Contains similar JWT validation logic
   
   Match 2: TokenHandler.cs:L1-L100 (similarity: 0.82)  
   - Token parsing and validation utilities
   
   Match 3: AuthController.cs:L123-L222 (similarity: 0.78)
   - Related authentication endpoints
   ```

7. **Review context generated**:
   The AI reviewer gets the original file changes PLUS the 3 similar code chunks as context, enabling it to:
   - Check for consistency with existing JWT patterns
   - Suggest improvements based on similar implementations
   - Identify potential security issues by comparing approaches

## 🚀 Performance Optimizations

### Current Optimizations
1. **File Limits**: Only process 50 files per repository (cost control)
2. **Skip Patterns**: Ignore binary files, node_modules, etc.
3. **Chunk Size**: 100 lines balances context vs. granularity
4. **Overlap**: 10 lines prevents context loss at boundaries
5. **Similarity Threshold**: 0.7 filters out irrelevant matches

### Potential Improvements
1. **Caching**: Persist embeddings to avoid re-indexing
2. **Incremental Updates**: Only re-index changed files
3. **Vector Database**: Use specialized DB for better performance
4. **Hierarchical Search**: Pre-filter by file type/directory
5. **Async Processing**: Generate embeddings in parallel

## 🧰 Available Tools

### 1. Embedding Visualization
```bash
dotnet run -- --visualize-embeddings
# Creates interactive HTML files showing chunk relationships
```

### 2. Embedding Inspection  
```bash
dotnet run -- --inspect-embeddings
# Shows actual vector values and similarity calculations
```

### 3. Memory Inspector (New)
```bash
dotnet run -- --inspect-memory
# Shows what's stored in memory and how search works
```

### 4. Web Interface
```bash
dotnet run --web
# Navigate to http://localhost:5000 for UI-based reviews
```

## 💡 Key Insights

1. **Memory is the Database**: No external vector database - everything in RAM
2. **Overlap Prevents Loss**: 10-line overlap ensures no context is lost between chunks  
3. **Similarity is Everything**: Cosine similarity (0.7+ threshold) determines relevance
4. **Cost-Controlled**: Limited to 50 files to manage embedding API costs
5. **Real-Time Search**: Sub-second search across hundreds of code chunks
6. **Context-Aware**: Search uses actual PR changes, not just filenames
7. **Cross-Language**: Embeddings understand semantic similarity across programming languages

## 🔧 Debugging Tips

### If Search Returns No Results:
```bash
# Check if repository is indexed
# Look for: "Repository 'xyz' is NOT indexed!"

# Check similarity scores
# Look for: "Closest match was: 0.65 at Program.cs:L1-L100"
# (Score below 0.7 threshold)

# Check query construction  
# Look for: "Search query is empty - no added lines in diff"
```

### If Memory Usage is High:
```bash
# Check chunk count
# Look for: "Code chunks created: 1247"  
# (More than expected - maybe large files not filtered)

# Check embedding dimensions
# Look for: "Vector dimension: 3072"
# (Should be 3072 for text-embedding-3-large)
```

### If Search is Slow:
```bash
# Check chunk count vs. time
# Look for: "Calculating cosine similarity against 1500+ chunks"
# (Too many chunks - consider better file filtering)
```

## 🎯 Summary

The codebase chunking and memory flow transforms your repository into an **intelligent, searchable knowledge base**:

- **Input**: Repository files from Azure DevOps
- **Processing**: 100-line chunks with 10-line overlap  
- **Storage**: In-memory dictionary of embedding vectors
- **Search**: Real-time semantic similarity using cosine distance
- **Output**: Contextually relevant code for AI review

This enables the AI to understand not just the changed file, but how it relates to the entire codebase - making reviews more accurate, consistent, and insightful.

---

*Use the inspection tools to see exactly what's happening with your codebase in memory!*