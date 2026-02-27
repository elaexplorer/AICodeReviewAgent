# Azure Container Apps Deployment Script for Code Review Agent
# This script automates the deployment to Azure Container Apps

param(
    [Parameter(Mandatory=$false)]
    [string]$ResourceGroup = "rg-code-review-agent",
    
    [Parameter(Mandatory=$false)]
    [string]$Location = "eastus",
    
    [Parameter(Mandatory=$false)]
    [string]$AcrName = "acrcodereview$(Get-Random -Minimum 1000 -Maximum 9999)",
    
    [Parameter(Mandatory=$false)]
    [string]$ContainerAppName = "code-review-agent",
    
    [Parameter(Mandatory=$false)]
    [string]$EnvironmentName = "code-review-env"
)

# Color output functions
function Write-Success { param($message) Write-Host "✓ $message" -ForegroundColor Green }
function Write-Info { param($message) Write-Host "ℹ $message" -ForegroundColor Cyan }
function Write-Warning { param($message) Write-Host "⚠ $message" -ForegroundColor Yellow }
function Write-Error { param($message) Write-Host "✗ $message" -ForegroundColor Red }

Write-Host ""
Write-Host "========================================" -ForegroundColor Magenta
Write-Host "  Code Review Agent - Azure Deployment" -ForegroundColor Magenta
Write-Host "========================================" -ForegroundColor Magenta
Write-Host ""

# Step 1: Check prerequisites
Write-Info "Checking prerequisites..."

# Check if Azure CLI is installed
if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    Write-Error "Azure CLI is not installed. Please install from: https://aka.ms/installazurecli"
    exit 1
}
Write-Success "Azure CLI found"

# Check if Docker is running
try {
    docker ps | Out-Null
    Write-Success "Docker is running"
} catch {
    Write-Error "Docker is not running. Please start Docker Desktop."
    exit 1
}

# Check if logged into Azure
Write-Info "Checking Azure login status..."
$account = az account show 2>$null | ConvertFrom-Json
if (-not $account) {
    Write-Warning "Not logged into Azure. Logging in..."
    az login
    $account = az account show | ConvertFrom-Json
}
Write-Success "Logged in as: $($account.user.name)"
Write-Info "Subscription: $($account.name)"

# Step 2: Load environment variables
Write-Info "Loading environment variables..."

# Try to load from .env file if it exists
$envFile = ".env"
$envVars = @{}

if (Test-Path $envFile) {
    Write-Success "Found .env file"
    Get-Content $envFile | ForEach-Object {
        if ($_ -match '^\s*([^#][^=]+)=(.+)$') {
            $key = $matches[1].Trim()
            $value = $matches[2].Trim().Trim('"').Trim("'")
            $envVars[$key] = $value
        }
    }
} else {
    Write-Warning "No .env file found. Will use environment variables."
}

# Get required environment variables
function Get-EnvVar {
    param($name, $required = $true)
    
    $value = $envVars[$name]
    if (-not $value) {
        $value = [Environment]::GetEnvironmentVariable($name)
    }
    
    if ($required -and -not $value) {
        Write-Error "Missing required environment variable: $name"
        exit 1
    }
    
    return $value
}

$azureOpenAiEndpoint = Get-EnvVar "AZURE_OPENAI_ENDPOINT" $false
$azureOpenAiKey = Get-EnvVar "AZURE_OPENAI_API_KEY" $false
$openAiKey = Get-EnvVar "OPENAI_API_KEY" $false
$azureOpenAiDeployment = Get-EnvVar "AZURE_OPENAI_DEPLOYMENT" $false
$adoPat = Get-EnvVar "ADO_PAT" $true
$adoOrg = Get-EnvVar "ADO_ORGANIZATION" $false

if (-not $azureOpenAiDeployment) {
    $azureOpenAiDeployment = "gpt-4"
}

if (-not $adoOrg) {
    $adoOrg = "SPOOL"
}

# Validate AI provider configuration
if (-not $azureOpenAiEndpoint -and -not $openAiKey) {
    Write-Error "Either AZURE_OPENAI_ENDPOINT or OPENAI_API_KEY must be configured"
    exit 1
}

if ($azureOpenAiEndpoint -and -not $azureOpenAiKey) {
    Write-Error "AZURE_OPENAI_API_KEY is required when using Azure OpenAI"
    exit 1
}

Write-Success "Environment variables validated"

# Step 3: Create resource group
Write-Info "Creating resource group '$ResourceGroup' in '$Location'..."
az group create --name $ResourceGroup --location $Location --output none
if ($LASTEXITCODE -eq 0) {
    Write-Success "Resource group created"
} else {
    Write-Error "Failed to create resource group"
    exit 1
}

# Step 4: Create Azure Container Registry
Write-Info "Creating Azure Container Registry '$AcrName'..."
az acr create `
    --resource-group $ResourceGroup `
    --name $AcrName `
    --sku Basic `
    --admin-enabled true `
    --output none

if ($LASTEXITCODE -eq 0) {
    Write-Success "Container Registry created"
} else {
    Write-Error "Failed to create Container Registry"
    exit 1
}

# Step 5: Build and push container image
Write-Info "Building and pushing container image to ACR..."
Write-Info "This may take several minutes..."

az acr build `
    --registry $AcrName `
    --image code-review-agent:latest `
    --image code-review-agent:v1.0 `
    --file Dockerfile `
    . `
    --output table

if ($LASTEXITCODE -eq 0) {
    Write-Success "Container image built and pushed"
} else {
    Write-Error "Failed to build container image"
    exit 1
}

# Step 6: Get ACR credentials
Write-Info "Retrieving ACR credentials..."
$acrServer = az acr show --name $AcrName --query loginServer -o tsv
$acrUsername = az acr credential show --name $AcrName --query username -o tsv
$acrPassword = az acr credential show --name $AcrName --query "passwords[0].value" -o tsv
Write-Success "ACR credentials retrieved"

# Step 7: Create Container Apps Environment
Write-Info "Creating Container Apps Environment '$EnvironmentName'..."
az containerapp env create `
    --name $EnvironmentName `
    --resource-group $ResourceGroup `
    --location $Location `
    --output none

if ($LASTEXITCODE -eq 0) {
    Write-Success "Container Apps Environment created"
} else {
    Write-Error "Failed to create Container Apps Environment"
    exit 1
}

# Step 8: Create Container App with secrets
Write-Info "Creating Container App '$ContainerAppName'..."

# Build secrets array
$secrets = @(
    "ado-pat=$adoPat"
)

if ($azureOpenAiKey) {
    $secrets += "azure-openai-key=$azureOpenAiKey"
}

if ($openAiKey) {
    $secrets += "openai-key=$openAiKey"
}

$secrets += "acr-password=$acrPassword"

# Build environment variables array
$envVars = @(
    "ADO_ORGANIZATION=$adoOrg",
    "ADO_PAT=secretref:ado-pat",
    "ASPNETCORE_ENVIRONMENT=Production"
)

if ($azureOpenAiEndpoint) {
    $envVars += "AZURE_OPENAI_ENDPOINT=$azureOpenAiEndpoint"
    $envVars += "AZURE_OPENAI_API_KEY=secretref:azure-openai-key"
    $envVars += "AZURE_OPENAI_DEPLOYMENT=$azureOpenAiDeployment"
}

if ($openAiKey) {
    $envVars += "OPENAI_API_KEY=secretref:openai-key"
}

# Create the container app
az containerapp create `
    --name $ContainerAppName `
    --resource-group $ResourceGroup `
    --environment $EnvironmentName `
    --image "$acrServer/code-review-agent:latest" `
    --target-port 8080 `
    --ingress external `
    --registry-server $acrServer `
    --registry-username $acrUsername `
    --registry-password $acrPassword `
    --secrets ($secrets -join " ") `
    --env-vars ($envVars -join " ") `
    --cpu 1 `
    --memory 2Gi `
    --min-replicas 0 `
    --max-replicas 5 `
    --output none

if ($LASTEXITCODE -eq 0) {
    Write-Success "Container App created"
} else {
    Write-Error "Failed to create Container App"
    exit 1
}

# Step 9: Get the application URL
Write-Info "Retrieving application URL..."
$appUrl = az containerapp show `
    --name $ContainerAppName `
    --resource-group $ResourceGroup `
    --query "properties.configuration.ingress.fqdn" `
    -o tsv

Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host "  Deployment Successful!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host ""
Write-Success "Application URL: https://$appUrl"
Write-Host ""
Write-Info "Available endpoints:"
Write-Host "  • Health Check: https://$appUrl/api/codereview/config/status"
Write-Host "  • Web UI:       https://$appUrl/"
Write-Host "  • Review PR:    POST https://$appUrl/api/codereview/start"
Write-Host ""

Write-Info "Testing health endpoint..."
Start-Sleep -Seconds 5

try {
    $response = Invoke-WebRequest -Uri "https://$appUrl/api/codereview/config/status" -UseBasicParsing
    if ($response.StatusCode -eq 200) {
        Write-Success "Health check passed! Application is running."
    }
} catch {
    Write-Warning "Health check failed. Application may still be starting up."
    Write-Info "Wait a few moments and try: https://$appUrl/api/codereview/config/status"
}

Write-Host ""
Write-Info "Deployment Details:"
Write-Host "  • Resource Group: $ResourceGroup"
Write-Host "  • Container Registry: $AcrName"
Write-Host "  • Container App: $ContainerAppName"
Write-Host "  • Location: $Location"
Write-Host ""

Write-Info "To view logs, run:"
Write-Host "  az containerapp logs show --name $ContainerAppName --resource-group $ResourceGroup --follow" -ForegroundColor Yellow
Write-Host ""

Write-Info "To update the app after code changes, run:"
Write-Host "  .\update-container-app.ps1 -ResourceGroup $ResourceGroup -AcrName $AcrName -ContainerAppName $ContainerAppName" -ForegroundColor Yellow
Write-Host ""
