#!/bin/bash

# Test script for Code Review Agent
# This script demonstrates how to use the code review agent

echo "Code Review Agent Test Script"
echo "=============================="
echo

# Check if environment variables are set
if [ -z "$OPENAI_API_KEY" ]; then
    echo "❌ OPENAI_API_KEY environment variable is not set"
    echo "Please set your OpenAI API key:"
    echo "export OPENAI_API_KEY=\"your-api-key-here\""
    exit 1
fi

if [ -z "$ADO_ORGANIZATION" ]; then
    echo "❌ ADO_ORGANIZATION environment variable is not set"
    echo "Please set your Azure DevOps organization:"
    echo "export ADO_ORGANIZATION=\"your-org-name\""
    exit 1
fi

if [ -z "$ADO_PAT" ]; then
    echo "❌ ADO_PAT environment variable is not set"
    echo "Please set your Azure DevOps Personal Access Token:"
    echo "export ADO_PAT=\"your-pat-here\""
    exit 1
fi

echo "✅ Environment variables are set"
echo "Organization: $ADO_ORGANIZATION"
echo "API Key: ${OPENAI_API_KEY:0:10}..."
echo "PAT: ${ADO_PAT:0:10}..."
echo

# Check if project and PR ID are provided
if [ "$#" -ne 2 ]; then
    echo "Usage: $0 <project-name> <pull-request-id>"
    echo "Example: $0 MyProject 123"
    exit 1
fi

PROJECT=$1
PR_ID=$2

echo "Testing Code Review Agent with:"
echo "Project: $PROJECT"
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
echo "dotnet run --configuration Release -- \"$PROJECT\" $PR_ID"
echo

dotnet run --configuration Release -- "$PROJECT" $PR_ID

if [ $? -eq 0 ]; then
    echo
    echo "✅ Code review completed successfully!"
else
    echo
    echo "❌ Code review failed"
    exit 1
fi