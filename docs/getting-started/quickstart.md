# 🚀 Quick Start Guide

**Get RAG Code Intelligence running in 5 minutes**

## Prerequisites

- ✅ .NET 9.0 SDK
- ✅ Git
- ✅ Azure OpenAI or OpenAI API key
- ✅ Azure DevOps organization (for code review features)

## Step 1: Clone and Setup

```bash
# Clone the repository
git clone https://github.com/your-org/rag-code-intelligence.git
cd rag-code-intelligence

# Restore dependencies
dotnet restore
```

## Step 2: Configure Environment

Create a `.env` file in the root directory:

```bash
# AI Provider Configuration
AZURE_OPENAI_ENDPOINT=https://your-resource.openai.azure.com/
AZURE_OPENAI_API_KEY=your-api-key-here
AZURE_OPENAI_DEPLOYMENT=gpt-4
AZURE_OPENAI_EMBEDDING_DEPLOYMENT=text-embedding-ada-002

# Azure DevOps Configuration (optional)
ADO_ORGANIZATION=your-organization
ADO_PAT=your-personal-access-token

# Logging Level (optional)
LOGGING_LEVEL=Information
```

## Step 3: Run the Application

```bash
# Start the application
dotnet run

# Or with hot reload for development
dotnet watch run
```

The application will start on `http://localhost:5001`

## Step 4: Test with Sample Repository

### Option A: Index Your Own Repository

```bash
# Index a repository (replace with your values)
curl -X POST "http://localhost:5001/api/repositories/index" \
  -H "Content-Type: application/json" \
  -d '{
    "project": "YourProject",
    "repositoryId": "your-repo-id",
    "branch": "main"
  }'
```

### Option B: Use Demo Mode

```bash
# Run with demo data (no Azure DevOps required)
dotnet run --demo
```

## Step 5: Query Your Code

Once indexing completes, you can query your codebase:

### Via Web API

```bash
# Ask a question about your code
curl -X POST "http://localhost:5001/api/query" \
  -H "Content-Type: application/json" \
  -d '{
    "question": "How do I authenticate users?",
    "repositoryId": "your-repo-id"
  }'
```

### Via Web Interface

Open `http://localhost:5001/query` in your browser for the interactive interface.

## Step 6: Review Pull Requests (Optional)

If you configured Azure DevOps:

```bash
# Review a pull request
curl -X POST "http://localhost:5001/api/review" \
  -H "Content-Type: application/json" \
  -d '{
    "project": "YourProject",
    "repositoryId": "your-repo-id", 
    "pullRequestId": 123
  }'
```

## 🎉 Success!

If everything worked, you should see:

✅ Repository successfully indexed  
✅ Semantic search responding to queries  
✅ Context-aware answers about your code  

## Next Steps

- 📖 **Learn More**: [RAG Fundamentals](../concepts/rag-fundamentals.md)
- 🔧 **Configure**: [Configuration Guide](configuration.md)
- 🏗️ **Deploy**: [Production Deployment](../deployment/docker.md)
- 🐛 **Issues?**: [Troubleshooting](../support/troubleshooting.md)

## Common Issues

### "No embeddings generated"
**Solution**: Check your Azure OpenAI endpoint and API key in `.env`

### "Repository not found"
**Solution**: Verify your Azure DevOps PAT has repository read permissions

### "Port already in use"
**Solution**: Use a different port: `dotnet run --urls "http://localhost:5002"`

### "Out of memory during indexing"
**Solution**: Limit file processing in `appsettings.json`:
```json
{
  "RAG": {
    "MaxFilesToProcess": 100,
    "ChunkSize": 50
  }
}
```

---

**Need help?** Check our [FAQ](../support/faq.md) or [get support](../support/getting-help.md).