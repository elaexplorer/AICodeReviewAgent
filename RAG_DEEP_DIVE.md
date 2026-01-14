# RAG (Retrieval Augmented Generation) Deep Dive

## What is RAG?

RAG is a technique that enhances AI responses by retrieving relevant context from a knowledge base before generating a response. Instead of relying solely on the AI's training data, RAG allows the AI to access and use current, specific information from your codebase.

## How RAG Works in Code Review

### 1. **Indexing Phase** (Done Once Per Repository)

```
┌─────────────────────────────────────────────────────────────┐
│ INDEXING: Building the Knowledge Base                       │
└─────────────────────────────────────────────────────────────┘

Step 1: Fetch All Files
├─ Call Azure DevOps API to get repository file tree
├─ Filter out binary files, node_modules, etc.
└─ Result: List of .cs, .py, .rs files

Step 2: Chunk Each File
├─ Split large files into 100-line chunks with 10-line overlap
├─ Why chunks? Large files don't fit in embedding models
└─ Result: ~1000 code chunks from 50 files

Step 3: Generate Embeddings
├─ For each chunk, call OpenAI Embedding API
├─ Converts code text → 1536-dimensional vector
├─ Vectors capture semantic meaning of code
└─ Result: Each chunk has a float[1536] embedding

Step 4: Store in Vector Database
├─ In-memory dictionary (repositoryId → chunks)
├─ Production: Use Qdrant, Pinecone, or Azure AI Search
└─ Result: Searchable knowledge base
```

### 2. **Retrieval Phase** (Done For Each File Review)

```
┌─────────────────────────────────────────────────────────────┐
│ RETRIEVAL: Finding Relevant Context                         │
└─────────────────────────────────────────────────────────────┘

Step 1: Build Search Query
├─ Extract added lines from PR diff
├─ Include file name and context
└─ Example: "public class UserService { async Task<User> GetUser..."

Step 2: Generate Query Embedding
├─ Convert search query → 1536-dimensional vector
└─ Same embedding model as indexing

Step 3: Semantic Search (Cosine Similarity)
├─ Compare query vector with all chunk vectors
├─ Formula: similarity = dot(A, B) / (||A|| * ||B||)
├─ Values range from -1 to 1 (1 = identical)
└─ Filter results with similarity > 0.7

Step 4: Rank and Return
├─ Sort by similarity score descending
├─ Take top 3-5 most relevant chunks
└─ Return code snippets with metadata
```

### 3. **Augmentation Phase** (Adding Context to AI Prompt)

```
┌─────────────────────────────────────────────────────────────┐
│ AUGMENTATION: Enhancing the AI Prompt                       │
└─────────────────────────────────────────────────────────────┘

Original Prompt:
"Review this code change: [diff]"

Augmented Prompt:
"Review this code change: [diff]

## Relevant Codebase Context

### Similar code (relevance: 0.85)
Location: Services/AuthService.cs:L45-L145
```csharp
public class AuthService {
  // Similar authentication patterns
}
```

### Similar code (relevance: 0.78)
Location: Controllers/UserController.cs:L22-L122
```csharp
public class UserController {
  // Similar API patterns
}
```

## Related Files (Dependencies)
### Models/User.cs
[First 20 lines of dependent file]
"
```

## Why RAG is Powerful

### Without RAG:
- AI only knows general programming patterns
- No context about YOUR codebase
- Can't see similar implementations
- May suggest patterns that conflict with your style

### With RAG:
- ✅ AI sees how you handle authentication in other files
- ✅ Knows your error handling patterns
- ✅ Understands your naming conventions
- ✅ Can reference actual implementations from your repo
- ✅ Gives context-aware suggestions

## Example: Real RAG in Action

**File Being Reviewed:** `Services/PaymentService.cs`
```csharp
+ public async Task<Payment> ProcessPayment(Order order) {
+   var result = await _api.Charge(order.Total);
+   return new Payment { Status = result.Status };
+ }
```

**RAG Retrieves:**
1. `Services/OrderService.cs` (similarity: 0.89)
   - Shows error handling pattern: try/catch with logging

2. `Services/EmailService.cs` (similarity: 0.82)
   - Shows null checks before API calls

3. `Models/Payment.cs` (dependency)
   - Shows Payment model has more fields than just Status

**AI Review With RAG Context:**
"⚠️ Missing error handling. Based on `OrderService.cs`, all API calls should be wrapped in try/catch with logging. Also, `Payment.cs` shows the model has `TransactionId` and `Timestamp` fields that should be populated."

**Without RAG:**
"✅ Looks good. Consider adding error handling." (generic advice)

## Implementation Details

### Vector Similarity Math
```csharp
// Cosine Similarity measures angle between vectors
// Range: -1 (opposite) to 1 (identical)
double CosineSimilarity(float[] A, float[] B) {
    double dotProduct = 0;
    double magA = 0, magB = 0;

    for (int i = 0; i < A.Length; i++) {
        dotProduct += A[i] * B[i];
        magA += A[i] * A[i];
        magB += B[i] * B[i];
    }

    return dotProduct / (Math.Sqrt(magA) * Math.Sqrt(magB));
}
```

### Chunking Strategy
```
File: 500 lines
Chunk Size: 100 lines
Overlap: 10 lines

Chunks:
1. Lines 1-100
2. Lines 91-190    (10 line overlap with chunk 1)
3. Lines 181-280   (10 line overlap with chunk 2)
4. Lines 271-370
5. Lines 361-460
6. Lines 451-500

Why overlap? Prevents splitting related code across chunks.
```

### Embedding Cost & Performance

**Indexing (one-time per repo):**
- 50 files × 5 chunks/file = 250 chunks
- 250 embedding API calls
- Cost: ~$0.01 (text-embedding-3-small)
- Time: ~30 seconds

**Retrieval (per file review):**
- 1 embedding API call for query
- In-memory vector search: <10ms
- Cost: ~$0.0001
- Time: ~100ms total

## Current Implementation Status

✅ **Implemented:**
- Vector storage (in-memory dictionary)
- Semantic search with cosine similarity
- Chunking with overlap
- Dependency parsing (.cs, .py, .rs)
- Embedding generation

❌ **Not Yet Working:**
- Repository indexing (returns 0 files)
- Need to fix Azure DevOps API call
- Need to trigger indexing when PR is opened

🔧 **Next Steps:**
1. Fix GetRepositoryItemsAsync to return actual files
2. Trigger IndexRepositoryAsync when reviewing a PR
3. Add comprehensive logging at each step
4. Test with real codebase
