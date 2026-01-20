# Code Review Agent - Deployment Guide

## Table of Contents
1. [Docker Containerization](#1-docker-containerization)
2. [Azure AI Agent Service Deployment](#2-azure-ai-agent-service-deployment)
3. [Azure Container Apps Deployment](#3-azure-container-apps-deployment)
4. [Environment Variables Reference](#4-environment-variables-reference)

---

## 1. Docker Containerization

### Prerequisites
- Docker Desktop installed
- Azure OpenAI or OpenAI API access
- Azure DevOps PAT with repository access

### Quick Start

```bash
# 1. Create .env file from example
cp .env.example .env

# 2. Edit .env with your credentials
nano .env

# 3. Build and run with docker-compose
docker-compose up --build -d

# 4. View logs
docker-compose logs -f

# 5. Access the application
open http://localhost:5001
```

### Manual Docker Build

```bash
# Build the image
docker build -t code-review-agent:latest .

# Run the container
docker run -d \
  --name code-review-agent \
  -p 5001:8080 \
  -e AZURE_OPENAI_ENDPOINT="https://your-resource.openai.azure.com/" \
  -e AZURE_OPENAI_API_KEY="your-key" \
  -e AZURE_OPENAI_DEPLOYMENT="gpt-4" \
  -e ADO_PAT="your-pat" \
  -e ADO_ORGANIZATION="your-org" \
  code-review-agent:latest
```

### Push to Container Registry

```bash
# Azure Container Registry
az acr login --name yourregistry
docker tag code-review-agent:latest yourregistry.azurecr.io/code-review-agent:latest
docker push yourregistry.azurecr.io/code-review-agent:latest

# Docker Hub
docker tag code-review-agent:latest yourusername/code-review-agent:latest
docker push yourusername/code-review-agent:latest
```

---

## 2. Azure AI Agent Service Deployment

Azure AI Agent Service provides a managed platform for deploying AI agents with built-in scaling, monitoring, and security.

### Prerequisites
- Azure subscription
- Azure CLI installed (`az`)
- Container image pushed to Azure Container Registry

### Step 1: Create Azure Resources

```bash
# Set variables
RESOURCE_GROUP="rg-code-review-agent"
LOCATION="eastus"
ACR_NAME="yourcontainerregistry"
AGENT_NAME="code-review-agent"

# Create resource group
az group create --name $RESOURCE_GROUP --location $LOCATION

# Create Azure Container Registry (if not exists)
az acr create --resource-group $RESOURCE_GROUP \
  --name $ACR_NAME --sku Basic

# Enable admin access
az acr update --name $ACR_NAME --admin-enabled true
```

### Step 2: Push Container to ACR

```bash
# Login to ACR
az acr login --name $ACR_NAME

# Build and push
az acr build --registry $ACR_NAME \
  --image code-review-agent:v1 .
```

### Step 3: Create Azure AI Foundry Project

```bash
# Create AI Foundry hub (if not exists)
az ml workspace create --name "ai-hub-code-review" \
  --resource-group $RESOURCE_GROUP \
  --kind hub

# Create AI Foundry project
az ml workspace create --name "code-review-project" \
  --resource-group $RESOURCE_GROUP \
  --kind project \
  --hub-id "/subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.MachineLearningServices/workspaces/ai-hub-code-review"
```

### Step 4: Deploy Agent to Azure AI Agent Service

Create `agent-deployment.yaml`:

```yaml
$schema: https://azuremlschemas.azureedge.net/latest/managedOnlineDeployment.schema.json
name: code-review-agent-deployment
endpoint_name: code-review-agent-endpoint
model:
  path: .
  type: custom_model
environment:
  image: yourregistry.azurecr.io/code-review-agent:v1
  inference_config:
    liveness_route:
      path: /api/codereview/config/status
      port: 8080
    readiness_route:
      path: /api/codereview/config/status
      port: 8080
    scoring_route:
      path: /api/codereview/start
      port: 8080
environment_variables:
  AZURE_OPENAI_ENDPOINT: "https://your-resource.openai.azure.com/"
  AZURE_OPENAI_DEPLOYMENT: "gpt-4"
  ADO_ORGANIZATION: "your-org"
instance_type: Standard_DS3_v2
instance_count: 1
```

Deploy:

```bash
# Create online endpoint
az ml online-endpoint create --name code-review-agent-endpoint \
  --resource-group $RESOURCE_GROUP \
  --workspace-name code-review-project

# Create deployment
az ml online-deployment create --file agent-deployment.yaml \
  --resource-group $RESOURCE_GROUP \
  --workspace-name code-review-project \
  --all-traffic

# Set secrets (do NOT put in yaml)
az ml online-deployment update \
  --name code-review-agent-deployment \
  --endpoint-name code-review-agent-endpoint \
  --set environment_variables.AZURE_OPENAI_API_KEY="$AZURE_OPENAI_API_KEY" \
  --set environment_variables.ADO_PAT="$ADO_PAT"
```

### Step 5: Configure Authentication

```bash
# Get endpoint URL and key
az ml online-endpoint show --name code-review-agent-endpoint \
  --resource-group $RESOURCE_GROUP \
  --workspace-name code-review-project \
  --query "scoring_uri" -o tsv

az ml online-endpoint get-credentials --name code-review-agent-endpoint \
  --resource-group $RESOURCE_GROUP \
  --workspace-name code-review-project
```

### Step 6: Test the Deployment

```bash
ENDPOINT_URL=$(az ml online-endpoint show --name code-review-agent-endpoint \
  --resource-group $RESOURCE_GROUP \
  --workspace-name code-review-project \
  --query "scoring_uri" -o tsv)

ENDPOINT_KEY=$(az ml online-endpoint get-credentials --name code-review-agent-endpoint \
  --resource-group $RESOURCE_GROUP \
  --workspace-name code-review-project \
  --query "primaryKey" -o tsv)

# Test health endpoint
curl -X GET "$ENDPOINT_URL/api/codereview/config/status" \
  -H "Authorization: Bearer $ENDPOINT_KEY"

# Test code review
curl -X POST "$ENDPOINT_URL/api/codereview/start" \
  -H "Authorization: Bearer $ENDPOINT_KEY" \
  -H "Content-Type: application/json" \
  -d '{"project": "SCC", "repository": "my-repo", "pullRequestId": 123}'
```

---

## 3. Azure Container Apps Deployment

Alternative deployment using Azure Container Apps for serverless scaling.

```bash
# Create Container App Environment
az containerapp env create \
  --name code-review-env \
  --resource-group $RESOURCE_GROUP \
  --location $LOCATION

# Deploy Container App
az containerapp create \
  --name code-review-agent \
  --resource-group $RESOURCE_GROUP \
  --environment code-review-env \
  --image yourregistry.azurecr.io/code-review-agent:v1 \
  --target-port 8080 \
  --ingress external \
  --registry-server yourregistry.azurecr.io \
  --secrets \
    azure-openai-key="$AZURE_OPENAI_API_KEY" \
    ado-pat="$ADO_PAT" \
  --env-vars \
    AZURE_OPENAI_ENDPOINT="https://your-resource.openai.azure.com/" \
    AZURE_OPENAI_API_KEY=secretref:azure-openai-key \
    AZURE_OPENAI_DEPLOYMENT="gpt-4" \
    ADO_PAT=secretref:ado-pat \
    ADO_ORGANIZATION="your-org" \
  --cpu 1 \
  --memory 2Gi \
  --min-replicas 0 \
  --max-replicas 5

# Get the URL
az containerapp show \
  --name code-review-agent \
  --resource-group $RESOURCE_GROUP \
  --query "properties.configuration.ingress.fqdn" -o tsv
```

---

## 4. Environment Variables Reference

| Variable | Required | Description |
|----------|----------|-------------|
| `AZURE_OPENAI_ENDPOINT` | Yes* | Azure OpenAI endpoint URL |
| `AZURE_OPENAI_API_KEY` | Yes* | Azure OpenAI API key |
| `AZURE_OPENAI_DEPLOYMENT` | No | Model deployment name (default: gpt-4) |
| `OPENAI_API_KEY` | Yes* | OpenAI API key (alternative to Azure) |
| `ADO_PAT` | Yes | Azure DevOps Personal Access Token |
| `ADO_ORGANIZATION` | No | ADO organization (default: SPOOL) |

*Either Azure OpenAI or OpenAI configuration is required.

---

## API Endpoints

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/api/codereview/start` | POST | Start a code review |
| `/api/codereview/index` | POST | Index a repository for RAG |
| `/api/codereview/index/status/{repoId}` | GET | Check indexing status |
| `/api/codereview/pullrequests/{project}/{repo}` | GET | List PRs (auto-triggers indexing) |
| `/api/codereview/config/status` | GET | Health check |
| `/api/codereview/config/validate` | POST | Validate ADO credentials |

---

## Monitoring & Logging

### View Container Logs

```bash
# Docker
docker logs -f code-review-agent

# Azure Container Apps
az containerapp logs show \
  --name code-review-agent \
  --resource-group $RESOURCE_GROUP \
  --follow

# Azure AI Agent Service
az ml online-deployment get-logs \
  --name code-review-agent-deployment \
  --endpoint-name code-review-agent-endpoint \
  --resource-group $RESOURCE_GROUP \
  --workspace-name code-review-project
```

### Key Logs to Monitor

- `RAG INDEXING: Starting` - Embedding generation started
- `AUTO-INDEX: Background Indexing Complete` - Repository indexed
- `LLM REQUEST/RESPONSE` - AI model calls with token usage
- `TOKEN USAGE` - Cost tracking for API calls
