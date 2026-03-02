# 🔌 API Reference

**Complete API documentation for RAG Code Intelligence**

## Base URL

```
http://localhost:5001/api
```

## Authentication

Currently uses Azure DevOps Personal Access Token (PAT) configured in environment variables. Future versions will support API keys and OAuth.

---

## 📁 Repository Management

### Index Repository

**POST** `/repositories/index`

Index a repository for semantic search.

#### Request Body
```json
{
  "project": "string",
  "repositoryId": "string", 
  "branch": "string",
  "forceReindex": false,
  "maxFiles": 1000
}
```

#### Response
```json
{
  "success": true,
  "repositoryId": "my-repo",
  "filesProcessed": 156,
  "chunksCreated": 1247,
  "indexingTime": "00:02:34",
  "message": "Repository indexed successfully"
}
```

#### Example
```bash
curl -X POST "http://localhost:5001/api/repositories/index" \
  -H "Content-Type: application/json" \
  -d '{
    "project": "MyProject",
    "repositoryId": "my-awesome-repo",
    "branch": "main",
    "forceReindex": false
  }'
```

### Get Repository Status

**GET** `/repositories/{repositoryId}/status`

Check if a repository is indexed and get statistics.

#### Response
```json
{
  "repositoryId": "my-repo",
  "isIndexed": true,
  "totalChunks": 1247,
  "filesIndexed": 156,
  "lastIndexed": "2024-12-20T10:30:00Z",
  "indexingStatus": "completed",
  "languages": ["C#", "JavaScript", "Python"],
  "statistics": {
    "avgChunksPerFile": 8.0,
    "totalTokens": 156834,
    "embeddingDimension": 1536
  }
}
```

---

## 🔍 Query & Search

### Semantic Query

**POST** `/query`

Ask questions about your codebase using natural language.

#### Request Body
```json
{
  "question": "string",
  "repositoryId": "string",
  "maxResults": 5,
  "includeContext": true,
  "language": "any"
}
```

#### Response
```json
{
  "question": "How do I authenticate users?",
  "answer": "Based on your codebase, you can authenticate users using the AuthenticationService...",
  "confidence": 0.92,
  "sources": [
    {
      "filePath": "Services/AuthenticationService.cs",
      "startLine": 15,
      "endLine": 28,
      "similarity": 0.95,
      "content": "public async Task<User> AuthenticateAsync..."
    }
  ],
  "responseTime": "234ms"
}
```

#### Example
```bash
curl -X POST "http://localhost:5001/api/query" \
  -H "Content-Type: application/json" \
  -d '{
    "question": "How do I handle user authentication?",
    "repositoryId": "my-repo",
    "maxResults": 5
  }'
```

### Similarity Search

**POST** `/search/similar`

Find code similar to a given code snippet.

#### Request Body
```json
{
  "code": "string",
  "repositoryId": "string",
  "language": "csharp",
  "maxResults": 10
}
```

#### Response
```json
{
  "results": [
    {
      "filePath": "Controllers/UserController.cs",
      "similarity": 0.94,
      "startLine": 42,
      "endLine": 58,
      "content": "Similar code snippet...",
      "language": "csharp"
    }
  ]
}
```

---

## 📝 Code Review

### Review Pull Request

**POST** `/review/pullrequest`

Automatically review a pull request using RAG context.

#### Request Body
```json
{
  "project": "string",
  "repositoryId": "string",
  "pullRequestId": 123,
  "autoPost": false,
  "reviewTypes": ["security", "performance", "best-practices"]
}
```

#### Response
```json
{
  "pullRequestId": 123,
  "reviewStatus": "completed",
  "totalComments": 5,
  "commentsPosted": 3,
  "summary": {
    "securityIssues": 1,
    "performanceIssues": 2,
    "bestPracticeViolations": 2,
    "overallRating": "good"
  },
  "comments": [
    {
      "filePath": "Controllers/UserController.cs",
      "lineNumber": 47,
      "commentText": "Consider validating input before processing...",
      "severity": "warning",
      "category": "security",
      "suggestions": ["Add input validation", "Use parameterized queries"]
    }
  ]
}
```

### Get Review History

**GET** `/review/pullrequest/{pullRequestId}/history`

Get review history for a pull request.

---

## 📊 Analytics & Monitoring

### Repository Analytics

**GET** `/analytics/repository/{repositoryId}`

Get analytics for a specific repository.

#### Response
```json
{
  "repositoryId": "my-repo",
  "period": "last30days",
  "metrics": {
    "totalQueries": 234,
    "avgResponseTime": "156ms",
    "popularTopics": [
      {"topic": "authentication", "count": 45},
      {"topic": "database", "count": 32}
    ],
    "codeHealth": {
      "duplicateCodePercentage": 12.5,
      "complexityScore": 7.2,
      "testCoverage": 84.3
    }
  }
}
```

### System Health

**GET** `/health`

Check system health and status.

#### Response
```json
{
  "status": "healthy",
  "timestamp": "2024-12-20T10:30:00Z",
  "components": {
    "database": "healthy",
    "embeddingService": "healthy", 
    "azureDevOps": "healthy"
  },
  "performance": {
    "avgResponseTime": "156ms",
    "activeRepositories": 12,
    "totalQueries24h": 1247
  }
}
```

---

## 🔧 Configuration

### Get Configuration

**GET** `/config`

Get current system configuration.

### Update Configuration

**POST** `/config`

Update system configuration (admin only).

---

## 📈 Batch Operations

### Batch Index Repositories

**POST** `/batch/index`

Index multiple repositories in parallel.

#### Request Body
```json
{
  "repositories": [
    {
      "project": "Project1",
      "repositoryId": "repo1",
      "branch": "main"
    },
    {
      "project": "Project2", 
      "repositoryId": "repo2",
      "branch": "develop"
    }
  ],
  "maxConcurrency": 3
}
```

### Batch Query

**POST** `/batch/query`

Execute multiple queries in parallel.

---

## 🚨 Error Responses

All endpoints return consistent error responses:

```json
{
  "error": {
    "code": "REPOSITORY_NOT_FOUND",
    "message": "Repository 'my-repo' has not been indexed",
    "details": "Please index the repository first using POST /repositories/index",
    "timestamp": "2024-12-20T10:30:00Z",
    "requestId": "abc-123-def"
  }
}
```

### Common Error Codes

| Code | Description |
|------|-------------|
| `REPOSITORY_NOT_INDEXED` | Repository needs to be indexed first |
| `INVALID_QUERY` | Query format or content is invalid |
| `RATE_LIMIT_EXCEEDED` | Too many requests, slow down |
| `EMBEDDING_SERVICE_ERROR` | Issue with AI embedding service |
| `AZURE_DEVOPS_ERROR` | Problem accessing Azure DevOps |

---

## 📚 SDKs & Examples

### C# SDK

```csharp
var client = new RAGClient("http://localhost:5001");

// Index repository
await client.IndexRepositoryAsync("MyProject", "my-repo", "main");

// Query codebase
var result = await client.QueryAsync("How do I authenticate users?", "my-repo");
Console.WriteLine(result.Answer);
```

### Python SDK

```python
from rag_client import RAGClient

client = RAGClient("http://localhost:5001")

# Query codebase
result = client.query("How do I handle errors?", "my-repo")
print(result.answer)
```

### JavaScript SDK

```javascript
const RAGClient = require('@your-org/rag-client');

const client = new RAGClient('http://localhost:5001');

// Query codebase
const result = await client.query({
  question: 'How do I validate input?',
  repositoryId: 'my-repo'
});

console.log(result.answer);
```

---

## 🔄 Webhooks

### Repository Update Webhook

Automatically re-index repositories when code changes.

**POST** `/webhooks/repository-updated`

#### Payload
```json
{
  "eventType": "repository.updated",
  "repository": {
    "project": "MyProject",
    "id": "my-repo",
    "branch": "main"
  },
  "changes": {
    "filesModified": ["Controllers/UserController.cs"],
    "filesAdded": ["Services/NewService.cs"],
    "filesDeleted": []
  }
}
```

---

**Need more examples?** Check our [integration guide](../integration/code-examples.md) or [framework-specific docs](../integration/frameworks.md).