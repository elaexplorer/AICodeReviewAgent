#!/bin/bash
# Test script for Code Review Agent
# This script demonstrates how to use the code review agent

echo "Code Review Agent Test Script"
echo "=============================="
echo

# Check if environment variables are set
HAS_OPENAI=false
HAS_AZURE_OPENAI=false

if [ ! -z "$OPENAI_API_KEY" ]; then
    HAS_OPENAI=true
fi

if [ ! -z "$AZURE_OPENAI_ENDPOINT" ] && [ ! -z "$AZURE_OPENAI_API_KEY" ]; then
    HAS_AZURE_OPENAI=true
fi

if [ "$HAS_OPENAI" = false ] && [ "$HAS_AZURE_OPENAI" = false ]; then
    echo "❌ Neither OpenAI nor Azure OpenAI is configured"
    echo "Please configure one of the following:"
    echo
    echo "Option 1 - OpenAI:"
    echo "export OPENAI_API_KEY=\"your-api-key-here\""
    echo
    echo "Option 2 - Azure OpenAI:"
    echo "export AZURE_OPENAI_ENDPOINT=\"https://your-resource.openai.azure.com/\""
    echo "export AZURE_OPENAI_API_KEY=\"your-azure-api-key\""
    echo "export AZURE_OPENAI_DEPLOYMENT=\"gpt-4\"  # optional"
    exit 1
fi

if [ -z "$ADO_ORGANIZATION" ]; then
    echo "ℹ️  ADO_ORGANIZATION not set, using default: SPOOL"
    ADO_ORGANIZATION="SPOOL"
fi

if [ -z "$ADO_PAT" ]; then
    echo "❌ ADO_PAT environment variable is not set"
    echo "Please set your Azure DevOps Personal Access Token:"
    echo "export ADO_PAT=\"your-pat-here\""
    exit 1
fi

echo "✅ Environment variables are set"
echo "Organization: $ADO_ORGANIZATION"

if [ "$HAS_AZURE_OPENAI" = true ]; then
    echo "AI Service: Azure OpenAI"
    echo "Endpoint: $AZURE_OPENAI_ENDPOINT"
    echo "Deployment: ${AZURE_OPENAI_DEPLOYMENT:-gpt-4}"
    echo "API Key: ${AZURE_OPENAI_API_KEY:0:10}..."
else
    echo "AI Service: OpenAI"
    echo "API Key: ${OPENAI_API_KEY:0:10}..."
fi

echo "PAT: ${ADO_PAT:0:10}..."
echo

# Check if repository and PR ID are provided
if [ "$#" -eq 2 ]; then
    # Format: <repository> <pull-request-id>
    # Use default project SCC
    PROJECT="SCC"
    REPOSITORY=$1
    PR_ID=$2
    echo "Using default project: SCC"
elif [ "$#" -eq 3 ]; then
    # Format: <project> <repository> <pull-request-id>
    PROJECT=$1
    REPOSITORY=$2
    PR_ID=$3
else
    echo "Usage: $0 <repository-name> <pull-request-id>"
    echo "       $0 <project-name> <repository-name> <pull-request-id>"
    echo "Example: $0 MyRepo 123"
    echo "Example: $0 MyProject MyRepo 123"
    exit 1
fi

echo "Testing Code Review Agent with:"
echo "Project: $PROJECT"
echo "Repository: $REPOSITORY"
echo "Pull Request ID: $PR_ID"
echo

# Build the project
echo "Building the project..."
dotnet build --configuration Release

if [ $? -ne 0 ]; then
    echo "❌ Build failed"
    exit 1
fi

echo "✅ Build successful"
echo

# Run the code review agent
echo "Running code review agent..."
echo "dotnet run --configuration Release -- \"$PROJECT\" \"$REPOSITORY\" $PR_ID"
echo

dotnet run --configuration Release -- "$PROJECT" "$REPOSITORY" $PR_ID

if [ $? -eq 0 ]; then
    echo
    echo "✅ Code review completed successfully!"
else
    echo
    echo "❌ Code review failed"
    exit 1
fi