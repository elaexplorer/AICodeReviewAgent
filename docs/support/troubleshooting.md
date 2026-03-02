# 🐛 Troubleshooting Guide

**Common issues and solutions for RAG Code Intelligence**

## Quick Diagnostic

```bash
# Check system health
curl http://localhost:5001/health

# View recent logs
docker-compose logs --tail=50 rag-code-intelligence

# Check service status
docker-compose ps
```

---

## 🚨 Common Issues

### 1. Repository Indexing Problems

#### ❌ "Repository indexing failed" or "No files found"

**Symptoms:**
- `GET /repositories/{id}/status` shows `isIndexed: false`
- Indexing API returns 0 files processed
- Error logs show "No files found in repository"

**Solutions:**

**Check Azure DevOps Configuration:**
```bash
# Verify environment variables
docker exec rag-code-intelligence env | grep ADO
# Should show:
# ADO_ORGANIZATION=your-org
# ADO_PAT=your-token
```

**Verify PAT Permissions:**
- ✅ Code (read)
- ✅ Project and team (read)  
- ✅ Repository (read)

**Test API Connection:**
```bash
# Test Azure DevOps REST API
curl -u ":${ADO_PAT}" \
  "https://dev.azure.com/${ADO_ORGANIZATION}/${PROJECT}/_apis/git/repositories/${REPO_ID}/items?api-version=7.0&recursionLevel=Full"
```

**Force Git Clone Method:**
```json
{
  "project": "YourProject",
  "repositoryId": "your-repo", 
  "branch": "main",
  "forceGitClone": true
}
```

#### ❌ "Git clone failed" 

**Symptoms:**
- Git clone approach fails
- Logs show authentication errors

**Solutions:**

**Configure Git Credentials:**
```bash
# Set up git credentials in container
docker exec -it rag-code-intelligence git config --global credential.helper store

# Or use SSH keys
docker run -v ~/.ssh:/root/.ssh rag-code-intelligence
```

**Use HTTPS with PAT:**
```bash
# Repository URL format
https://username:${PAT}@dev.azure.com/organization/project/_git/repository
```

#### ❌ "Out of memory during indexing"

**Symptoms:**
- Container restarts during indexing
- Logs show OutOfMemoryException
- Docker stats show 100% memory usage

**Solutions:**

**Increase Container Memory:**
```yaml
services:
  rag-code-intelligence:
    deploy:
      resources:
        limits:
          memory: 8G  # Increase from default 4G
```

**Reduce Batch Size:**
```json
{
  "maxFiles": 100,        // Reduce from default
  "chunkSize": 50,       // Smaller chunks  
  "batchSize": 5         // Process fewer files at once
}
```

**Process Repository in Parts:**
```bash
# Index specific file patterns
curl -X POST "/api/repositories/index" \
  -d '{"project": "MyProject", "repositoryId": "repo", "includePatterns": ["*.cs", "*.js"]}'
```

---

### 2. AI Service Connection Issues

#### ❌ "Embedding generation failed" or "OpenAI API error"

**Symptoms:**
- Queries return "No context found"
- Logs show embedding API failures
- 429 rate limit errors

**Solutions:**

**Verify API Configuration:**
```bash
# Check environment variables
docker exec rag-code-intelligence env | grep AZURE_OPENAI
# Should show endpoint, key, and deployment names
```

**Test API Connection:**
```bash
# Test embedding endpoint
curl -X POST "${AZURE_OPENAI_ENDPOINT}/openai/deployments/${EMBEDDING_DEPLOYMENT}/embeddings?api-version=2023-05-15" \
  -H "api-key: ${AZURE_OPENAI_API_KEY}" \
  -H "Content-Type: application/json" \
  -d '{"input": "test"}'
```

**Handle Rate Limits:**
```yaml
environment:
  - EMBEDDING_BATCH_SIZE=5     # Reduce batch size
  - EMBEDDING_DELAY_MS=1000    # Add delay between requests
  - MAX_RETRIES=5              # Increase retry count
```

**Switch to Different Region/Model:**
```bash
# Try different endpoint or deployment
AZURE_OPENAI_ENDPOINT=https://other-region.openai.azure.com/
AZURE_OPENAI_DEPLOYMENT=text-embedding-3-small  # Different model
```

#### ❌ "Invalid API response" or "Model not found"

**Solutions:**

**Check Model Deployment:**
```bash
# List available deployments
curl "${AZURE_OPENAI_ENDPOINT}/openai/deployments?api-version=2023-05-15" \
  -H "api-key: ${AZURE_OPENAI_API_KEY}"
```

**Update API Version:**
```yaml
environment:
  - AZURE_OPENAI_API_VERSION=2024-02-15-preview  # Latest version
```

---

### 3. Query & Search Issues

#### ❌ "No relevant results found" or poor search quality

**Symptoms:**
- Queries return empty results
- Search results don't match expected code
- Low similarity scores (<0.5)

**Solutions:**

**Verify Repository is Indexed:**
```bash
curl http://localhost:5001/api/repositories/your-repo/status
# Check: isIndexed: true, totalChunks > 0
```

**Improve Query Phrasing:**
```bash
# Instead of: "authentication"
# Try: "how to authenticate users" or "user login validation"

# Instead of: "database"  
# Try: "save data to database" or "query user information"
```

**Check Embedding Quality:**
```bash
# Debug endpoint to see embedding vectors
curl -X POST "http://localhost:5001/api/debug/embedding" \
  -d '{"text": "your query here"}'
```

**Adjust Search Parameters:**
```json
{
  "question": "your question",
  "repositoryId": "your-repo",
  "maxResults": 10,        // Increase results
  "minSimilarity": 0.3,    // Lower threshold
  "includeContext": true
}
```

#### ❌ "Query timeout" or slow responses

**Solutions:**

**Optimize Vector Search:**
```yaml
environment:
  - VECTOR_SEARCH_TIMEOUT=30000    # 30 seconds
  - MAX_CONCURRENT_SEARCHES=5      # Limit concurrent searches
  - ENABLE_SEARCH_CACHE=true       # Cache search results
```

**Use Parallel Processing:**
```json
{
  "question": "your question",
  "repositoryId": "your-repo", 
  "parallelSearch": true,
  "searchStrategy": "hybrid"    // Use multiple search methods
}
```

---

### 4. Performance Issues

#### ❌ High memory usage or slow performance

**Symptoms:**
- Container uses >4GB RAM constantly
- Response times >5 seconds
- System becomes unresponsive

**Solutions:**

**Memory Optimization:**
```yaml
environment:
  - DOTNET_gcServer=true           # Server GC mode
  - DOTNET_GCRetainVM=true        # Retain virtual memory
  - VECTOR_CACHE_SIZE_MB=1024     # Limit vector cache
  - CLEANUP_INTERVAL_MINUTES=30   # Regular cleanup
```

**Database Optimization:**
```bash
# Clear old cached data
curl -X POST "http://localhost:5001/api/admin/clear-cache"

# Compact vector database
curl -X POST "http://localhost:5001/api/admin/compact"
```

**Resource Monitoring:**
```bash
# Monitor resource usage
docker stats rag-code-intelligence

# Check disk usage
docker exec rag-code-intelligence df -h

# Monitor memory patterns
docker exec rag-code-intelligence cat /proc/meminfo
```

#### ❌ "Request timeout" errors

**Solutions:**

**Increase Timeouts:**
```yaml
environment:
  - REQUEST_TIMEOUT_SECONDS=300    # 5 minutes for indexing
  - QUERY_TIMEOUT_SECONDS=30       # 30 seconds for queries
  - HTTP_CLIENT_TIMEOUT=120        # HTTP client timeout
```

**Optimize Request Processing:**
```yaml
environment:
  - MAX_CONCURRENT_REQUESTS=5      # Limit concurrent requests
  - ENABLE_REQUEST_QUEUING=true    # Queue excess requests
  - THREAD_POOL_MIN_THREADS=10     # Minimum thread pool size
```

---

### 5. Docker & Container Issues

#### ❌ Container startup failures

**Symptoms:**
- Container exits immediately
- "Failed to bind to address" errors
- Health checks failing

**Solutions:**

**Check Port Conflicts:**
```bash
# Find what's using port 5001
netstat -tlnp | grep :5001
lsof -i :5001

# Use different port
docker run -p 5002:5001 rag-code-intelligence
```

**Fix File Permissions:**
```bash
# Fix data directory permissions
sudo chown -R 1000:1000 ./data
chmod -R 755 ./data
```

**Check Container Logs:**
```bash
# View startup logs
docker-compose logs rag-code-intelligence

# Follow logs in real-time
docker-compose logs -f rag-code-intelligence
```

#### ❌ Volume mounting issues

**Solutions:**

**Fix Volume Paths:**
```yaml
volumes:
  - ./data:/app/data          # Relative path
  - /opt/rag/data:/app/data   # Absolute path
```

**SELinux Context (Linux):**
```bash
# Add SELinux context for Docker volumes
sudo setsebool -P container_manage_cgroup on
```

---

## 🔍 Diagnostic Commands

### System Information
```bash
# Container environment
docker exec rag-code-intelligence env

# System resources
docker exec rag-code-intelligence cat /proc/meminfo
docker exec rag-code-intelligence df -h

# Application version
curl http://localhost:5001/api/version
```

### Health Checks
```bash
# Comprehensive health check
curl -s http://localhost:5001/health | jq '.'

# Component-specific health
curl -s http://localhost:5001/health/detailed | jq '.'

# Performance metrics
curl -s http://localhost:5001/metrics
```

### Log Analysis
```bash
# Search for errors in logs
docker-compose logs rag-code-intelligence 2>&1 | grep -i error

# Filter by log level
docker-compose logs rag-code-intelligence 2>&1 | grep "ERROR\|WARN"

# Extract timing information
docker-compose logs rag-code-intelligence 2>&1 | grep -E "took|elapsed"
```

---

## 🚨 Emergency Procedures

### Complete System Reset
```bash
# Stop all services
docker-compose down

# Remove all data (WARNING: This deletes everything!)
docker volume rm rag-data
docker volume rm rag-logs

# Rebuild and restart
docker-compose build --no-cache
docker-compose up -d
```

### Backup Before Reset
```bash
# Backup data volume
docker run --rm -v rag-data:/data -v $(pwd):/backup alpine tar czf /backup/emergency-backup.tar.gz -C /data .

# Backup configuration
cp .env emergency-env-backup
cp docker-compose.yml emergency-compose-backup
```

### Recovery Process
```bash
# Restore from backup
docker run --rm -v rag-data:/data -v $(pwd):/backup alpine tar xzf /backup/emergency-backup.tar.gz -C /data

# Restart services
docker-compose up -d

# Verify recovery
curl http://localhost:5001/health
```

---

## 📞 Getting Additional Help

### Debug Mode
```yaml
# docker-compose.debug.yml
version: '3.8'
services:
  rag-code-intelligence:
    environment:
      - ASPNETCORE_ENVIRONMENT=Development
      - LOGGING_LEVEL=Debug
      - ENABLE_DETAILED_LOGGING=true
      - ENABLE_PERFORMANCE_COUNTERS=true
```

### Collect Support Information
```bash
#!/bin/bash
# collect-support-info.sh

echo "RAG Code Intelligence Support Information" > support-info.txt
echo "Generated: $(date)" >> support-info.txt
echo "=======================================" >> support-info.txt

# System information
echo -e "\n=== System Information ===" >> support-info.txt
docker --version >> support-info.txt
docker-compose --version >> support-info.txt
uname -a >> support-info.txt

# Container status
echo -e "\n=== Container Status ===" >> support-info.txt
docker-compose ps >> support-info.txt

# Recent logs
echo -e "\n=== Recent Logs ===" >> support-info.txt
docker-compose logs --tail=100 rag-code-intelligence >> support-info.txt

# Configuration (sanitized)
echo -e "\n=== Configuration ===" >> support-info.txt
cat .env | sed 's/=.*/=***REDACTED***/' >> support-info.txt

# Health status
echo -e "\n=== Health Status ===" >> support-info.txt
curl -s http://localhost:5001/health >> support-info.txt

echo "Support information saved to support-info.txt"
```

### Contact Options
- 💬 **GitHub Issues**: [Report a bug](https://github.com/elaexplorer/AICodeReviewAgent/issues)
- 📧 **Email Support**: [support@your-org.com](mailto:support@your-org.com)
- 💼 **Enterprise Support**: [enterprise@your-org.com](mailto:enterprise@your-org.com)
- 📖 **Documentation**: [docs.your-org.com](https://docs.your-org.com)

---

**Still having issues?** Please include:
1. Output of `collect-support-info.sh`
2. Steps to reproduce the problem  
3. Expected vs. actual behavior
4. Your environment details (OS, Docker version, etc.)