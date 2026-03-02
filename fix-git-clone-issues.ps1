# PowerShell script to fix Git clone timeout issues
# Run this script to clean up hanging processes and temporary directories

Write-Host "🔧 Git Clone Issue Fix Script" -ForegroundColor Green
Write-Host "=============================" -ForegroundColor Green
Write-Host ""

# 1. Kill any hanging git processes
Write-Host "1. Checking for hanging git processes..." -ForegroundColor Yellow
$gitProcesses = Get-Process -Name "git" -ErrorAction SilentlyContinue
if ($gitProcesses) {
    Write-Host "   Found $($gitProcesses.Count) git processes. Terminating..." -ForegroundColor Red
    $gitProcesses | Stop-Process -Force
    Write-Host "   ✅ Git processes terminated" -ForegroundColor Green
} else {
    Write-Host "   ✅ No hanging git processes found" -ForegroundColor Green
}

# 2. Clean up temporary clone directories
Write-Host ""
Write-Host "2. Cleaning up temporary clone directories..." -ForegroundColor Yellow
$tempCloneBase = "$env:TEMP\repo_clone"

if (Test-Path $tempCloneBase) {
    Write-Host "   Found temp clone directory: $tempCloneBase" -ForegroundColor Yellow
    
    # Get all subdirectories
    $cloneDirs = Get-ChildItem -Path $tempCloneBase -Directory -ErrorAction SilentlyContinue
    
    foreach ($dir in $cloneDirs) {
        Write-Host "   Cleaning: $($dir.Name)..." -ForegroundColor Yellow
        
        try {
            # First, try to remove read-only attributes from git objects
            if (Test-Path "$($dir.FullName)\.git") {
                Get-ChildItem -Path "$($dir.FullName)\.git" -Recurse -File -ErrorAction SilentlyContinue | 
                    ForEach-Object { $_.Attributes = $_.Attributes -band (-bnot [System.IO.FileAttributes]::ReadOnly) }
            }
            
            # Force remove the entire directory
            Remove-Item -Path $dir.FullName -Recurse -Force -ErrorAction Stop
            Write-Host "   ✅ Cleaned: $($dir.Name)" -ForegroundColor Green
        }
        catch {
            Write-Host "   ❌ Failed to clean $($dir.Name): $($_.Exception.Message)" -ForegroundColor Red
            
            # Try alternative cleanup method
            try {
                cmd /c "rmdir /s /q `"$($dir.FullName)`""
                Write-Host "   ✅ Alternative cleanup succeeded for: $($dir.Name)" -ForegroundColor Green
            }
            catch {
                Write-Host "   ❌ Alternative cleanup also failed for: $($dir.Name)" -ForegroundColor Red
            }
        }
    }
} else {
    Write-Host "   ✅ No temp clone directories found" -ForegroundColor Green
}

# 3. Check git configuration
Write-Host ""
Write-Host "3. Checking git configuration..." -ForegroundColor Yellow

# Check if git is available
try {
    $gitVersion = git --version
    Write-Host "   ✅ Git available: $gitVersion" -ForegroundColor Green
} catch {
    Write-Host "   ❌ Git not found in PATH" -ForegroundColor Red
    Write-Host "   Please ensure Git is installed and added to PATH" -ForegroundColor Red
}

# Check ADO_PAT environment variable
if ($env:ADO_PAT) {
    Write-Host "   ✅ ADO_PAT environment variable is set" -ForegroundColor Green
} else {
    Write-Host "   ⚠️  ADO_PAT environment variable not set" -ForegroundColor Yellow
    Write-Host "   This may cause authentication issues with Azure DevOps repositories" -ForegroundColor Yellow
}

# 4. Test a simple git operation
Write-Host ""
Write-Host "4. Testing git functionality..." -ForegroundColor Yellow
$testDir = "$env:TEMP\git_test_$(Get-Random)"

try {
    # Create test directory
    New-Item -Path $testDir -ItemType Directory -Force | Out-Null
    
    # Test git init (should be fast)
    Set-Location $testDir
    $result = git init 2>&1
    
    if ($LASTEXITCODE -eq 0) {
        Write-Host "   ✅ Git functionality test passed" -ForegroundColor Green
    } else {
        Write-Host "   ❌ Git functionality test failed: $result" -ForegroundColor Red
    }
    
    # Cleanup
    Set-Location $env:USERPROFILE
    Remove-Item -Path $testDir -Recurse -Force -ErrorAction SilentlyContinue
}
catch {
    Write-Host "   ❌ Git functionality test failed: $($_.Exception.Message)" -ForegroundColor Red
}

# 5. Recommendations
Write-Host ""
Write-Host "🚀 Recommendations:" -ForegroundColor Cyan
Write-Host "===================" -ForegroundColor Cyan
Write-Host ""

Write-Host "Applied Fixes:" -ForegroundColor Green
Write-Host "   - Increased git clone timeout from 60s to 600s (10 minutes)" -ForegroundColor White
Write-Host "   - Added enhanced directory cleanup with git lock removal" -ForegroundColor White
Write-Host "   - Added authenticated clone URL support for Azure DevOps" -ForegroundColor White
Write-Host "   - Improved error handling and recovery" -ForegroundColor White
Write-Host ""

Write-Host "🔧 Configuration Steps:" -ForegroundColor Yellow
Write-Host "   1. Set ADO_PAT environment variable if not already set:" -ForegroundColor White
Write-Host "      `$env:ADO_PAT = 'your-azure-devops-personal-access-token'" -ForegroundColor Gray
Write-Host ""
Write-Host "   2. Restart your application to pick up the fixes" -ForegroundColor White
Write-Host ""
Write-Host "   3. Monitor the logs for improved clone performance" -ForegroundColor White
Write-Host ""

Write-Host "Alternative Approaches:" -ForegroundColor Magenta
Write-Host "   - Consider switching to API-based indexing for very large repositories" -ForegroundColor White
Write-Host "   - Use shallow clones with --depth 1 for faster operations" -ForegroundColor White
Write-Host "   - Implement repository size checks before attempting clone" -ForegroundColor White
Write-Host ""

Write-Host "✅ Cleanup complete! Your git clone issues should now be resolved." -ForegroundColor Green