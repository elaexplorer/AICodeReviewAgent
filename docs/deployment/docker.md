# 🐳 Docker Deployment Guide

**Deploy RAG Code Intelligence using Docker containers**

## Prerequisites

- ✅ Docker 20.10+
- ✅ Docker Compose 2.0+
- ✅ 4GB+ available RAM
- ✅ Azure OpenAI or OpenAI API key

---

## Quick Deployment

### Option 1: Docker Compose (Recommended)

1. **Create docker-compose.yml**
```yaml
version: '3.8'

services:
  rag-code-intelligence:
    image: your-org/rag-code-intelligence:latest
    ports:
      - "5001:5001"
    environment:
      # AI Configuration
      - AZURE_OPENAI_ENDPOINT=${AZURE_OPENAI_ENDPOINT}
      - AZURE_OPENAI_API_KEY=${AZURE_OPENAI_API_KEY}
      - AZURE_OPENAI_DEPLOYMENT=${AZURE_OPENAI_DEPLOYMENT}
      - AZURE_OPENAI_EMBEDDING_DEPLOYMENT=${AZURE_OPENAI_EMBEDDING_DEPLOYMENT}
      
      # Azure DevOps (Optional)
      - ADO_ORGANIZATION=${ADO_ORGANIZATION}
      - ADO_PAT=${ADO_PAT}
      
      # Application Settings
      - ASPNETCORE_ENVIRONMENT=Production
      - ASPNETCORE_URLS=http://+:5001
    volumes:
      - rag-data:/app/data
      - /var/run/docker.sock:/var/run/docker.sock:ro
    healthcheck:
      test: ["CMD", "curl", "-f", "http://localhost:5001/health"]
      interval: 30s
      timeout: 10s
      retries: 3
    restart: unless-stopped

volumes:
  rag-data:
    driver: local
```

2. **Create .env file**
```bash
# Copy from .env.example and fill in your values
AZURE_OPENAI_ENDPOINT=https://your-resource.openai.azure.com/
AZURE_OPENAI_API_KEY=your-api-key-here
AZURE_OPENAI_DEPLOYMENT=gpt-4
AZURE_OPENAI_EMBEDDING_DEPLOYMENT=text-embedding-ada-002
ADO_ORGANIZATION=your-organization
ADO_PAT=your-personal-access-token
```

3. **Start services**
```bash
docker-compose up -d
```

4. **Verify deployment**
```bash
# Check service status
docker-compose ps

# Check logs
docker-compose logs -f

# Test health endpoint
curl http://localhost:5001/health
```

### Option 2: Standalone Docker

```bash
# Pull the image
docker pull your-org/rag-code-intelligence:latest

# Run container
docker run -d \
  --name rag-code-intelligence \
  -p 5001:5001 \
  -e AZURE_OPENAI_ENDPOINT="your-endpoint" \
  -e AZURE_OPENAI_API_KEY="your-key" \
  -e AZURE_OPENAI_DEPLOYMENT="gpt-4" \
  -v rag-data:/app/data \
  your-org/rag-code-intelligence:latest
```

---

## Build from Source

### Build Docker Image

```bash
# Clone repository
git clone https://github.com/elaexplorer/AICodeReviewAgent.git
cd rag-code-intelligence

# Build image
docker build -t rag-code-intelligence:local .

# Or build with specific tag
docker build -t rag-code-intelligence:v1.0.0 .
```

### Multi-Stage Dockerfile

```dockerfile
# Build stage
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy project files
COPY ["CodeReviewAgent.csproj", "."]
RUN dotnet restore "CodeReviewAgent.csproj"

# Copy source code
COPY . .
RUN dotnet build "CodeReviewAgent.csproj" -c Release -o /app/build

# Publish stage
FROM build AS publish
RUN dotnet publish "CodeReviewAgent.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

# Install Node.js (for MCP support)
RUN apt-get update && apt-get install -y \
    nodejs \
    npm \
    curl \
    && rm -rf /var/lib/apt/lists/*

# Install Git (for repository cloning)
RUN apt-get update && apt-get install -y \
    git \
    && rm -rf /var/lib/apt/lists/*

# Copy application
COPY --from=publish /app/publish .

# Create data directory
RUN mkdir -p /app/data && chmod 755 /app/data

# Health check
HEALTHCHECK --interval=30s --timeout=10s --start-period=60s --retries=3 \
  CMD curl -f http://localhost:5001/health || exit 1

# Expose port
EXPOSE 5001

# Start application
ENTRYPOINT ["dotnet", "CodeReviewAgent.dll"]
```

---

## Production Configuration

### Environment Variables

#### Required
```bash
# AI Service
AZURE_OPENAI_ENDPOINT=https://your-resource.openai.azure.com/
AZURE_OPENAI_API_KEY=your-api-key
AZURE_OPENAI_DEPLOYMENT=gpt-4
AZURE_OPENAI_EMBEDDING_DEPLOYMENT=text-embedding-ada-002

# Application
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=http://+:5001
```

#### Optional
```bash
# Azure DevOps Integration
ADO_ORGANIZATION=your-organization
ADO_PAT=your-pat-token

# Logging
LOGGING_LEVEL=Information
STRUCTURED_LOGGING=true

# Performance
MAX_CONCURRENT_REQUESTS=10
REQUEST_TIMEOUT_SECONDS=300
EMBEDDING_BATCH_SIZE=20

# Storage
DATA_DIRECTORY=/app/data
ENABLE_PERSISTENT_CACHE=true
```

### Resource Limits

```yaml
services:
  rag-code-intelligence:
    # ... other config
    deploy:
      resources:
        limits:
          memory: 4G
          cpus: '2.0'
        reservations:
          memory: 2G
          cpus: '1.0'
```

### Persistent Storage

```yaml
volumes:
  # Application data
  rag-data:
    driver: local
    driver_opts:
      type: none
      o: bind
      device: /opt/rag-data

  # Logs
  rag-logs:
    driver: local
    driver_opts:
      type: none  
      o: bind
      device: /var/log/rag
```

---

## Scaling & Load Balancing

### Multi-Instance Deployment

```yaml
version: '3.8'

services:
  rag-app:
    image: your-org/rag-code-intelligence:latest
    deploy:
      replicas: 3
      update_config:
        parallelism: 1
        delay: 10s
      restart_policy:
        condition: on-failure
    environment:
      # ... your config
    
  nginx:
    image: nginx:alpine
    ports:
      - "80:80"
      - "443:443"
    volumes:
      - ./nginx.conf:/etc/nginx/nginx.conf:ro
      - ./ssl:/etc/nginx/ssl:ro
    depends_on:
      - rag-app
```

### NGINX Configuration

```nginx
upstream rag_backend {
    least_conn;
    server rag-app:5001 max_fails=3 fail_timeout=30s;
}

server {
    listen 80;
    server_name your-domain.com;
    
    location / {
        proxy_pass http://rag_backend;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        
        # Timeouts for long-running requests
        proxy_connect_timeout 60s;
        proxy_send_timeout 300s;
        proxy_read_timeout 300s;
    }
    
    location /health {
        proxy_pass http://rag_backend/health;
        access_log off;
    }
}
```

---

## Monitoring & Logging

### Health Checks

```yaml
services:
  rag-code-intelligence:
    # ... other config
    healthcheck:
      test: ["CMD", "curl", "-f", "http://localhost:5001/health"]
      interval: 30s
      timeout: 10s
      retries: 3
      start_period: 60s
```

### Logging with ELK Stack

```yaml
version: '3.8'

services:
  rag-app:
    # ... your app config
    logging:
      driver: "json-file"
      options:
        max-size: "100m"
        max-file: "5"
    
  elasticsearch:
    image: docker.elastic.co/elasticsearch/elasticsearch:8.5.0
    environment:
      - discovery.type=single-node
      - "ES_JAVA_OPTS=-Xms512m -Xmx512m"
    volumes:
      - elasticsearch-data:/usr/share/elasticsearch/data

  logstash:
    image: docker.elastic.co/logstash/logstash:8.5.0
    volumes:
      - ./logstash.conf:/usr/share/logstash/pipeline/logstash.conf:ro

  kibana:
    image: docker.elastic.co/kibana/kibana:8.5.0
    ports:
      - "5601:5601"
    environment:
      - ELASTICSEARCH_HOSTS=http://elasticsearch:9200

volumes:
  elasticsearch-data:
```

### Prometheus Metrics

```yaml
services:
  rag-app:
    # ... other config
    environment:
      - ENABLE_METRICS=true
      - METRICS_PORT=9090
    
  prometheus:
    image: prom/prometheus:latest
    ports:
      - "9090:9090"
    volumes:
      - ./prometheus.yml:/etc/prometheus/prometheus.yml:ro

  grafana:
    image: grafana/grafana:latest
    ports:
      - "3000:3000"
    environment:
      - GF_SECURITY_ADMIN_PASSWORD=admin
    volumes:
      - grafana-data:/var/lib/grafana

volumes:
  grafana-data:
```

---

## Security

### SSL/TLS Configuration

```yaml
services:
  nginx:
    # ... other config
    volumes:
      - ./ssl/cert.pem:/etc/nginx/ssl/cert.pem:ro
      - ./ssl/key.pem:/etc/nginx/ssl/key.pem:ro
```

### Secrets Management

```yaml
services:
  rag-app:
    # ... other config
    secrets:
      - azure_openai_key
      - ado_pat
    environment:
      - AZURE_OPENAI_API_KEY_FILE=/run/secrets/azure_openai_key
      - ADO_PAT_FILE=/run/secrets/ado_pat

secrets:
  azure_openai_key:
    file: ./secrets/azure_openai_key.txt
  ado_pat:
    file: ./secrets/ado_pat.txt
```

### Network Security

```yaml
networks:
  rag-network:
    driver: bridge
    internal: true
  
  public-network:
    driver: bridge

services:
  rag-app:
    networks:
      - rag-network
  
  nginx:
    networks:
      - rag-network
      - public-network
```

---

## Troubleshooting

### Common Issues

#### Container won't start
```bash
# Check logs
docker-compose logs rag-code-intelligence

# Check container status
docker-compose ps

# Inspect container
docker inspect rag-code-intelligence
```

#### Out of memory during indexing
```yaml
services:
  rag-app:
    deploy:
      resources:
        limits:
          memory: 8G  # Increase memory
    environment:
      - MAX_FILES_TO_PROCESS=100  # Reduce batch size
```

#### Slow response times
```yaml
services:
  rag-app:
    environment:
      - MAX_CONCURRENT_REQUESTS=5  # Reduce concurrency
      - EMBEDDING_BATCH_SIZE=10    # Smaller batches
```

### Debug Mode

```bash
# Run in debug mode
docker-compose -f docker-compose.yml -f docker-compose.debug.yml up

# Access container shell
docker exec -it rag-code-intelligence /bin/bash
```

---

## Backup & Disaster Recovery

### Data Backup

```bash
#!/bin/bash
# backup.sh

# Backup vector database
docker run --rm -v rag-data:/data -v $(pwd):/backup alpine tar czf /backup/rag-data-$(date +%Y%m%d).tar.gz -C /data .

# Backup configuration
cp .env backup/env-$(date +%Y%m%d).backup
cp docker-compose.yml backup/compose-$(date +%Y%m%d).backup
```

### Restore Process

```bash
#!/bin/bash
# restore.sh

# Stop services
docker-compose down

# Restore data
docker run --rm -v rag-data:/data -v $(pwd):/backup alpine tar xzf /backup/rag-data-20241220.tar.gz -C /data

# Start services
docker-compose up -d
```

---

**Next Steps:**
- 📊 [Monitoring & Observability](../advanced/monitoring.md)
- ☁️ [Azure Container Apps Deployment](azure.md)
- 🔧 [Production Configuration](production-config.md)
- 🐛 [Troubleshooting Guide](../support/troubleshooting.md)