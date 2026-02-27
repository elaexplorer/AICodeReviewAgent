# Azure Container Apps - Quick Deployment Guide

## Prerequisites

1. **Azure CLI** - [Install here](https://aka.ms/installazurecli)
2. **Docker Desktop** - Running and accessible
3. **Azure Subscription** - With permissions to create resources
4. **Environment Variables** - See configuration section below

## Configuration

### Step 1: Create .env file

If you don't have a `.env` file, create one:

```powershell
# Copy the example (if it doesn't exist, create it manually)
Copy-Item .env.example .env

# Edit with your actual credentials
notepad .env
```

### Required Variables in .env:

```env
# Azure OpenAI (recommended)
AZURE_OPENAI_ENDPOINT=https://your-resource.openai.azure.com/
AZURE_OPENAI_API_KEY=your-key-here
AZURE_OPENAI_DEPLOYMENT=gpt-4

# OR OpenAI (alternative)
# OPENAI_API_KEY=sk-your-key-here

# Azure DevOps (required)
ADO_PAT=your-personal-access-token
ADO_ORGANIZATION=your-org-name
```

## Deployment Options

### Option 1: Automated Deployment (Recommended)

Run the automated deployment script:

```powershell
# Deploy with default settings
.\deploy-to-azure.ps1

# Or customize the deployment
.\deploy-to-azure.ps1 `
    -ResourceGroup "my-resource-group" `
    -Location "eastus" `
    -ContainerAppName "my-code-review-agent"
```

**What it does:**
- ✓ Validates prerequisites (Azure CLI, Docker)
- ✓ Checks Azure login status
- ✓ Creates resource group
- ✓ Creates Azure Container Registry
- ✓ Builds and pushes Docker image
- ✓ Creates Container Apps Environment
- ✓ Deploys the Container App
- ✓ Configures secrets and environment variables
- ✓ Tests the deployment

### Option 2: Manual Deployment

#### Step 1: Login to Azure

```powershell
az login
```

#### Step 2: Set Variables

```powershell
$RESOURCE_GROUP = "rg-code-review-agent"
$LOCATION = "eastus"
$ACR_NAME = "acrcodereview$(Get-Random -Minimum 1000 -Maximum 9999)"
$CONTAINER_APP = "code-review-agent"
$ENVIRONMENT = "code-review-env"
```

#### Step 3: Create Resource Group

```powershell
az group create --name $RESOURCE_GROUP --location $LOCATION
```

#### Step 4: Create Container Registry

```powershell
az acr create `
    --resource-group $RESOURCE_GROUP `
    --name $ACR_NAME `
    --sku Basic `
    --admin-enabled true
```

#### Step 5: Build and Push Image

```powershell
az acr build `
    --registry $ACR_NAME `
    --image code-review-agent:latest `
    --file Dockerfile `
    .
```

#### Step 6: Get ACR Credentials

```powershell
$ACR_SERVER = az acr show --name $ACR_NAME --query loginServer -o tsv
$ACR_USERNAME = az acr credential show --name $ACR_NAME --query username -o tsv
$ACR_PASSWORD = az acr credential show --name $ACR_NAME --query "passwords[0].value" -o tsv
```

#### Step 7: Load Environment Variables

```powershell
# Load from .env file
Get-Content .env | ForEach-Object {
    if ($_ -match '^([^#][^=]+)=(.+)$') {
        $key = $matches[1].Trim()
        $value = $matches[2].Trim().Trim('"').Trim("'")
        Set-Variable -Name $key -Value $value
    }
}
```

#### Step 8: Create Container Apps Environment

```powershell
az containerapp env create `
    --name $ENVIRONMENT `
    --resource-group $RESOURCE_GROUP `
    --location $LOCATION
```

#### Step 9: Deploy Container App

```powershell
az containerapp create `
    --name $CONTAINER_APP `
    --resource-group $RESOURCE_GROUP `
    --environment $ENVIRONMENT `
    --image "$ACR_SERVER/code-review-agent:latest" `
    --target-port 8080 `
    --ingress external `
    --registry-server $ACR_SERVER `
    --registry-username $ACR_USERNAME `
    --registry-password $ACR_PASSWORD `
    --secrets `
        azure-openai-key=$AZURE_OPENAI_API_KEY `
        ado-pat=$ADO_PAT `
    --env-vars `
        AZURE_OPENAI_ENDPOINT=$AZURE_OPENAI_ENDPOINT `
        AZURE_OPENAI_API_KEY=secretref:azure-openai-key `
        AZURE_OPENAI_DEPLOYMENT=$AZURE_OPENAI_DEPLOYMENT `
        ADO_PAT=secretref:ado-pat `
        ADO_ORGANIZATION=$ADO_ORGANIZATION `
        ASPNETCORE_ENVIRONMENT=Production `
    --cpu 1 `
    --memory 2Gi `
    --min-replicas 0 `
    --max-replicas 5
```

#### Step 10: Get Application URL

```powershell
$APP_URL = az containerapp show `
    --name $CONTAINER_APP `
    --resource-group $RESOURCE_GROUP `
    --query "properties.configuration.ingress.fqdn" `
    -o tsv

Write-Host "Application URL: https://$APP_URL"
```

## Post-Deployment

### Test the Deployment

```powershell
# Health check
Invoke-WebRequest "https://$APP_URL/api/codereview/config/status"

# Web UI
Start-Process "https://$APP_URL"
```

### View Logs

```powershell
az containerapp logs show `
    --name $CONTAINER_APP `
    --resource-group $RESOURCE_GROUP `
    --follow
```

### Update After Code Changes

```powershell
.\update-container-app.ps1 `
    -ResourceGroup $RESOURCE_GROUP `
    -AcrName $ACR_NAME `
    -ContainerAppName $CONTAINER_APP
```

## API Endpoints

Once deployed, these endpoints are available:

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/api/codereview/config/status` | GET | Health check |
| `/api/codereview/start` | POST | Start code review |
| `/api/codereview/pullrequests/{project}/{repo}` | GET | List PRs |
| `/` | GET | Web UI |

## Troubleshooting

### Deployment fails with authentication error

```powershell
# Login again
az login

# Check current account
az account show
```

### Container app not starting

```powershell
# Check logs
az containerapp logs show --name $CONTAINER_APP --resource-group $RESOURCE_GROUP --follow

# Check revision status
az containerapp revision list --name $CONTAINER_APP --resource-group $RESOURCE_GROUP -o table
```

### Environment variables not loading

```powershell
# List current environment variables
az containerapp show --name $CONTAINER_APP --resource-group $RESOURCE_GROUP --query "properties.template.containers[0].env"

# Update environment variables
az containerapp update --name $CONTAINER_APP --resource-group $RESOURCE_GROUP --set-env-vars KEY=VALUE
```

### Image build fails

```powershell
# Build locally first to debug
docker build -t code-review-agent:test .

# Then push to ACR
az acr login --name $ACR_NAME
docker tag code-review-agent:test "$ACR_SERVER/code-review-agent:latest"
docker push "$ACR_SERVER/code-review-agent:latest"
```

## Cost Estimation

Azure Container Apps pricing (approximate):

- **Container Apps Environment**: ~$50/month
- **Container App (1 vCPU, 2GB RAM)**: ~$0.000012/second when running
- **With 0 min replicas**: Only pay when handling requests
- **Container Registry (Basic)**: ~$5/month

**Estimated monthly cost**: $55-100/month (depending on usage)

## Cleanup

To remove all resources:

```powershell
az group delete --name $RESOURCE_GROUP --yes --no-wait
```

## Next Steps

1. Configure Azure DevOps webhook to trigger reviews automatically
2. Set up Application Insights for monitoring
3. Configure custom domain
4. Enable authentication (Azure AD, etc.)

## Support

For issues or questions:
- Check logs: `az containerapp logs show --name $CONTAINER_APP --resource-group $RESOURCE_GROUP`
- Review [DEPLOYMENT.md](DEPLOYMENT.md) for detailed documentation
- Check [CONFIGURATION.md](CONFIGURATION.md) for environment variable help
