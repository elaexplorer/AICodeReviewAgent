# Update Azure Container App Script
# Use this script to update the running container app after making code changes

param(
    [Parameter(Mandatory=$true)]
    [string]$ResourceGroup,
    
    [Parameter(Mandatory=$true)]
    [string]$AcrName,
    
    [Parameter(Mandatory=$true)]
    [string]$ContainerAppName
)

function Write-Success { param($message) Write-Host "✓ $message" -ForegroundColor Green }
function Write-Info { param($message) Write-Host "ℹ $message" -ForegroundColor Cyan }
function Write-Error { param($message) Write-Host "✗ $message" -ForegroundColor Red }

Write-Host ""
Write-Host "========================================" -ForegroundColor Magenta
Write-Host "  Updating Container App" -ForegroundColor Magenta
Write-Host "========================================" -ForegroundColor Magenta
Write-Host ""

# Step 1: Build and push new image
Write-Info "Building and pushing updated container image..."
$version = Get-Date -Format "yyyyMMdd-HHmmss"

az acr build `
    --registry $AcrName `
    --image "code-review-agent:latest" `
    --image "code-review-agent:$version" `
    --file Dockerfile `
    . `
    --output table

if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to build container image"
    exit 1
}

Write-Success "Container image built and pushed with tag: $version"

# Step 2: Update container app
Write-Info "Updating Container App to use new image..."

$acrServer = az acr show --name $AcrName --query loginServer -o tsv

az containerapp update `
    --name $ContainerAppName `
    --resource-group $ResourceGroup `
    --image "$acrServer/code-review-agent:latest" `
    --output none

if ($LASTEXITCODE -eq 0) {
    Write-Success "Container App updated successfully"
} else {
    Write-Error "Failed to update Container App"
    exit 1
}

# Step 3: Get the application URL
$appUrl = az containerapp show `
    --name $ContainerAppName `
    --resource-group $ResourceGroup `
    --query "properties.configuration.ingress.fqdn" `
    -o tsv

Write-Host ""
Write-Success "Update complete!"
Write-Info "Application URL: https://$appUrl"
Write-Host ""
Write-Info "Testing health endpoint in 10 seconds..."
Start-Sleep -Seconds 10

try {
    $response = Invoke-WebRequest -Uri "https://$appUrl/api/codereview/config/status" -UseBasicParsing
    if ($response.StatusCode -eq 200) {
        Write-Success "Health check passed! Updated application is running."
    }
} catch {
    Write-Error "Health check failed. Check logs:"
    Write-Host "  az containerapp logs show --name $ContainerAppName --resource-group $ResourceGroup --follow" -ForegroundColor Yellow
}

Write-Host ""
