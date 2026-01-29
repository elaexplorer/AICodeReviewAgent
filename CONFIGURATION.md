# Configuration Guide

## Environment Variable Management

The Code Review Agent supports three ways to configure environment variables, checked in this priority order:

1. **`.env` file** (recommended for local development)
2. **System environment variables** (fallback)
3. **Default values** (for optional settings)

## Quick Start

### Option 1: Using .env File (Recommended)

```bash
# 1. Copy the example file
cp .env.example .env

# 2. Edit .env with your credentials
# Windows
notepad .env

# Linux/Mac
nano .env
# or
vim .env

# 3. Run the application (it automatically loads .env)
dotnet run my-repo 123
```

### Option 2: Using System Environment Variables

#### PowerShell (Windows)

```powershell
# Temporary (current session only)
$env:AZURE_OPENAI_ENDPOINT = "https://your-resource.openai.azure.com/"
$env:AZURE_OPENAI_API_KEY = "your-api-key"
$env:ADO_PAT = "your-pat-token"
$env:ADO_ORGANIZATION = "your-org"

# Persistent (user profile)
[Environment]::SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT", "https://your-resource.openai.azure.com/", "User")
[Environment]::SetEnvironmentVariable("AZURE_OPENAI_API_KEY", "your-key", "User")
[Environment]::SetEnvironmentVariable("ADO_PAT", "your-pat", "User")
[Environment]::SetEnvironmentVariable("ADO_ORGANIZATION", "your-org", "User")

# Note: Restart terminal after setting persistent variables
```

#### Bash/Linux/Mac

```bash
# Temporary (current session only)
export AZURE_OPENAI_ENDPOINT="https://your-resource.openai.azure.com/"
export AZURE_OPENAI_API_KEY="your-api-key"
export ADO_PAT="your-pat-token"
export ADO_ORGANIZATION="your-org"

# Persistent (add to ~/.bashrc or ~/.zshrc)
echo 'export AZURE_OPENAI_ENDPOINT="https://your-resource.openai.azure.com/"' >> ~/.bashrc
echo 'export AZURE_OPENAI_API_KEY="your-api-key"' >> ~/.bashrc
echo 'export ADO_PAT="your-pat-token"' >> ~/.bashrc
echo 'export ADO_ORGANIZATION="your-org"' >> ~/.bashrc

# Reload configuration
source ~/.bashrc
```

## Required Variables

| Variable | Required | Description |
|----------|----------|-------------|
| **AI Provider** (Choose ONE) | | |
| `AZURE_OPENAI_ENDPOINT` | Yes* | Azure OpenAI endpoint (e.g., `https://your-resource.openai.azure.com/`) |
| `AZURE_OPENAI_API_KEY` | Yes* | Your Azure OpenAI API key |
| `OPENAI_API_KEY` | Yes* | Your OpenAI API key (use instead of Azure) |
| **Azure DevOps** | | |
| `ADO_PAT` | **Yes** | Personal Access Token with Code (read) and PR (read/write) permissions |

*Either Azure OpenAI or OpenAI configuration required, not both.

## Optional Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `AZURE_OPENAI_DEPLOYMENT` | `gpt-4` | Azure OpenAI model deployment name |
| `AZURE_OPENAI_EMBEDDING_DEPLOYMENT` | `text-embedding-ada-002` | Embedding model deployment |
| `ADO_ORGANIZATION` | `SPOOL` | Azure DevOps organization name |
| `MCP_SERVER_URL` | `http://localhost:3000` | MCP server URL |

## Advanced Configuration

### Separate Embeddings Resource

If you use different Azure OpenAI resources for chat and embeddings:

```env
# Main chat resource
AZURE_OPENAI_ENDPOINT=https://chat-resource.openai.azure.com/
AZURE_OPENAI_API_KEY=your-chat-key
AZURE_OPENAI_DEPLOYMENT=gpt-4

# Separate embeddings resource
AZURE_OPENAI_EMBEDDING_ENDPOINT=https://embedding-resource.openai.azure.com/
AZURE_OPENAI_EMBEDDING_API_KEY=your-embedding-key
AZURE_OPENAI_EMBEDDING_DEPLOYMENT=text-embedding-ada-002
```

## Creating Azure DevOps PAT

1. Navigate to Azure DevOps → User Settings → Personal Access Tokens
2. Click "New Token"
3. Set required scopes:
   - **Code**: Read
   - **Pull Request**: Read & Write
   - **Project and Team**: Read
4. Copy the generated token (you won't see it again!)
5. Add to your `.env` file or environment variables

## Troubleshooting

### "No .env file found" Message

This is informational only. The application will use system environment variables instead.

### "Missing required environment variables" Error

**Check:**
1. Is your `.env` file in the project root directory?
2. Are variable names spelled correctly (case-sensitive)?
3. Are values properly quoted if they contain spaces?
4. Did you restart your terminal after setting system variables?

**Verify .env is loaded:**
```bash
# The application will show on startup:
# ✓ Loading environment variables from .env file
# OR
# ℹ No .env file found, using system environment variables
```

### .env File Not Loading

**Common issues:**
- File named `.env.example` instead of `.env`
- File not in project root directory
- File encoding issues (use UTF-8)

**Fix:**
```bash
# Verify file exists
ls -la .env  # Linux/Mac
dir .env     # Windows

# Check file location
pwd  # Should show project root
```

## Security Best Practices

1. **Never commit `.env` file** - It's already in `.gitignore`
2. **Use separate credentials** for different environments
3. **Rotate PAT tokens** regularly
4. **Limit PAT permissions** to minimum required scopes
5. **Use Azure Key Vault** for production deployments

## Examples

### Complete .env File Example

```env
# Azure OpenAI Configuration
AZURE_OPENAI_ENDPOINT=https://mycompany-openai.openai.azure.com/
AZURE_OPENAI_API_KEY=abc123def456ghi789jkl012mno345pqr678stu901
AZURE_OPENAI_DEPLOYMENT=gpt-4-turbo

# Azure DevOps Configuration
ADO_PAT=a1b2c3d4e5f6g7h8i9j0k1l2m3n4o5p6q7r8s9t0u1v2w3x4y5z6
ADO_ORGANIZATION=mycompany

# Optional: Custom embedding configuration
AZURE_OPENAI_EMBEDDING_DEPLOYMENT=text-embedding-3-large
```

### Using OpenAI Instead of Azure

```env
# OpenAI Configuration (alternative to Azure)
OPENAI_API_KEY=sk-proj-abc123def456ghi789jkl012mno345pqr678stu901

# Azure DevOps Configuration
ADO_PAT=a1b2c3d4e5f6g7h8i9j0k1l2m3n4o5p6q7r8s9t0u1v2w3x4y5z6
ADO_ORGANIZATION=mycompany
```

## Deployment Considerations

For production deployments, consider:
- Azure App Configuration
- Azure Key Vault
- Container secrets (Docker, Kubernetes)
- Azure Container Apps secrets

See [DEPLOYMENT.md](DEPLOYMENT.md) for detailed deployment instructions.
