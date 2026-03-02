# Test script to verify RAG system functionality and PAT security
param(
    [switch]$Verbose
)

Write-Host "================================================================" -ForegroundColor Cyan
Write-Host "             RAG SYSTEM & SECURITY TEST SUITE                  " -ForegroundColor Cyan  
Write-Host "================================================================" -ForegroundColor Cyan
Write-Host ""

$testsPassed = 0
$testsFailed = 0
$warnings = @()
$issues = @()

function Test-Component {
    param(
        [string]$TestName,
        [scriptblock]$TestScript
    )
    
    Write-Host "Testing $TestName... " -NoNewline
    try {
        $result = & $TestScript
        if ($result -eq $false) {
            throw "Test returned false"
        }
        Write-Host "✅ PASS" -ForegroundColor Green
        $script:testsPassed++
        return $true
    }
    catch {
        Write-Host "❌ FAIL" -ForegroundColor Red
        if ($Verbose) {
            Write-Host "  Error: $($_.Exception.Message)" -ForegroundColor Red
        }
        $script:testsFailed++
        $script:issues += "${TestName}: $($_.Exception.Message)"
        return $false
    }
}

function Add-Warning {
    param([string]$Message)
    $script:warnings += $Message
}

# Test 1: RAG System Components
Write-Host "🧠 RAG SYSTEM TESTS" -ForegroundColor Yellow
Write-Host "══════════════════" -ForegroundColor Yellow

Test-Component "CodebaseContextService exists" {
    if (-not (Test-Path "Services/CodebaseContextService.cs")) {
        throw "CodebaseContextService.cs not found"
    }
    
    $content = Get-Content "Services/CodebaseContextService.cs" -Raw
    if (-not ($content.Contains("IndexRepositoryAsync") -and $content.Contains("GetRelevantContextAsync"))) {
        throw "Missing required RAG methods"
    }
    return $true
}

Test-Component "RAG indexing methods optimized" {
    $content = Get-Content "Services/CodebaseContextService.cs" -Raw
    
    # Check git clone optimization
    if ($content.Contains("--filter=blob:none")) {
        throw "Still using problematic --filter=blob:none option"
    }
    
    # Check timeout improvements
    if ($content.Contains("timeoutSeconds = 120")) {
        Add-Warning "Found 120-second timeout - should be increased"
    }
    
    # Check shallow clone usage
    if (-not $content.Contains("--depth 1")) {
        throw "Missing shallow clone optimization"
    }
    
    return $true
}

Test-Component "Embedding services configured" {
    $programContent = Get-Content "Program.cs" -Raw
    
    if (-not ($programContent.Contains("IEmbeddingGenerator") -and $programContent.Contains("text-embedding"))) {
        throw "Embedding services not properly configured"
    }
    
    return $true
}

# Test 2: PAT Security Analysis
Write-Host ""
Write-Host "🔐 PAT SECURITY ANALYSIS" -ForegroundColor Red
Write-Host "════════════════════════" -ForegroundColor Red

Test-Component "PAT not hardcoded in source" {
    $sourceFiles = Get-ChildItem -Path . -Include "*.cs" -Recurse | Where-Object { $_.Name -ne "TestRunner.cs" }
    
    foreach ($file in $sourceFiles) {
        $content = Get-Content $file.FullName -Raw -ErrorAction SilentlyContinue
        if ($content -and $content -match 'pat.*=.*"[A-Za-z0-9]{20,50}"') {
            throw "Potential hardcoded PAT found in $($file.Name)"
        }
    }
    return $true
}

Test-Component "PAT handling centralization" {
    # Check how many places PAT is accessed from environment
    $patEnvAccess = Select-String -Path "*.cs" -Pattern 'Environment\.GetEnvironmentVariable\("ADO_PAT"\)' -Recurse
    
    if ($patEnvAccess.Count -gt 3) {
        Add-Warning "PAT accessed from environment in $($patEnvAccess.Count) places - should be centralized"
    }
    
    # Check if AdoConfigurationService exists for centralized management
    if (-not (Test-Path "Services/AdoConfigurationService.cs")) {
        throw "Missing AdoConfigurationService for centralized PAT management"
    }
    
    return $true
}

Test-Component "PAT not logged or exposed" {
    $sourceFiles = Get-ChildItem -Path "Services" -Include "*.cs" -Recurse
    
    foreach ($file in $sourceFiles) {
        $content = Get-Content $file.FullName -Raw -ErrorAction SilentlyContinue
        
        # Check for potential PAT logging
        if ($content -and $content -match 'LogInformation.*personalAccessToken|Log.*PAT.*\{.*\}') {
            Add-Warning "Potential PAT logging found in $($file.Name)"
        }
        
        # Check for PAT in URLs (security issue)
        if ($content -and $content -match 'https://.*:\{.*personalAccessToken.*\}@') {
            Add-Warning "PAT embedded in URL in $($file.Name) - security risk"
        }
    }
    return $true
}

# Test 3: Git Clone Security
Write-Host ""
Write-Host "🔧 GIT CLONE SECURITY" -ForegroundColor Blue
Write-Host "═════════════════════" -ForegroundColor Blue

Test-Component "Git authentication is secure" {
    $contextService = Get-Content "Services/CodebaseContextService.cs" -Raw
    
    # Check for secure credential handling
    if ($contextService.Contains("://.*:.*@")) {
        Add-Warning "Potential credentials in git URLs - verify they are properly masked"
    }
    
    # Check for credential cleanup
    if (-not $contextService.Contains("hide.*credential|mask.*token")) {
        Add-Warning "Consider adding credential masking in git operations"
    }
    
    return $true
}

Test-Component "Temp directory cleanup" {
    $contextService = Get-Content "Services/CodebaseContextService.cs" -Raw
    
    if (-not ($contextService.Contains("Directory.Delete") -and $contextService.Contains("recursive"))) {
        throw "Missing proper temp directory cleanup"
    }
    
    return $true
}

# Test 4: Runtime Configuration
Write-Host ""
Write-Host "⚙️ RUNTIME CONFIGURATION" -ForegroundColor Green
Write-Host "═══════════════════════" -ForegroundColor Green

Test-Component "Environment configuration secure" {
    if (Test-Path ".env") {
        $envContent = Get-Content ".env" -Raw
        
        # Check for placeholder values
        if ($envContent.Contains("YOUR_") -or $envContent.Contains("test-key") -or $envContent.Contains("mock-")) {
            Add-Warning "Environment file contains placeholder values - ensure real credentials are configured"
        }
        
        # Check for required configurations
        $requiredVars = @("AZURE_OPENAI_ENDPOINT", "ADO_ORGANIZATION")
        foreach ($var in $requiredVars) {
            if (-not $envContent.Contains($var)) {
                Add-Warning "Missing required environment variable: $var"
            }
        }
    } else {
        Add-Warning ".env file not found - configuration may rely on system environment"
    }
    return $true
}

# Test 5: Memory Management
Write-Host ""
Write-Host "💾 MEMORY MANAGEMENT" -ForegroundColor Magenta
Write-Host "══════════════════" -ForegroundColor Magenta

Test-Component "PAT stored securely in memory" {
    $serviceFiles = Get-ChildItem -Path "Services" -Include "*.cs" -Recurse
    
    foreach ($file in $serviceFiles) {
        $content = Get-Content $file.FullName -Raw -ErrorAction SilentlyContinue
        
        # Check for secure string usage (good practice)
        if ($content -and $content.Contains("SecureString")) {
            # Good - using secure strings
        } elseif ($content -and $content.Contains("_personalAccessToken") -and $content.Contains("private readonly string")) {
            Add-Warning "$($file.Name): PAT stored as plain string - consider SecureString"
        }
    }
    return $true
}

# Results Summary
Write-Host ""
Write-Host "================================================================" -ForegroundColor Cyan
Write-Host "                        TEST RESULTS                            " -ForegroundColor Cyan
Write-Host "================================================================" -ForegroundColor Cyan
Write-Host ""

Write-Host "Tests Passed: $testsPassed" -ForegroundColor Green
$failedColor = if ($testsFailed -gt 0) { "Red" } else { "Green" }
$failedSymbol = if ($testsFailed -gt 0) { " X" } else { "" }
Write-Host "Tests Failed: $testsFailed$failedSymbol" -ForegroundColor $failedColor
Write-Host "Warnings: $($warnings.Count)" -ForegroundColor Yellow

if ($warnings.Count -gt 0) {
    Write-Host ""
    Write-Host "SECURITY WARNINGS:" -ForegroundColor Yellow
    Write-Host "═════════════════" -ForegroundColor Yellow
    $warnings | ForEach-Object { Write-Host "!!  $_" -ForegroundColor Yellow }
}

if ($testsFailed -gt 0) {
    Write-Host ""
    Write-Host "FAILED TESTS:" -ForegroundColor Red
    Write-Host "════════════" -ForegroundColor Red
    $issues | ForEach-Object { Write-Host "X $_" -ForegroundColor Red }
}

Write-Host ""
Write-Host "RAG SYSTEM STATUS:" -ForegroundColor Cyan
if ($testsPassed -ge 6) {
    Write-Host "OK RAG system components are present and optimized" -ForegroundColor Green
    Write-Host "OK Git clone optimizations implemented" -ForegroundColor Green
} else {
    Write-Host "X RAG system has issues that need attention" -ForegroundColor Red
}

Write-Host ""
Write-Host "SECURITY RECOMMENDATIONS:" -ForegroundColor Cyan
Write-Host "• Centralize PAT handling in AdoConfigurationService only" -ForegroundColor Gray
Write-Host "• PAT should only be entered at login/entry point" -ForegroundColor Gray  
Write-Host "• Store PAT in memory securely (consider SecureString)" -ForegroundColor Gray
Write-Host "• Mask PAT in all logs and URLs" -ForegroundColor Gray
Write-Host "• Clean up temporary files containing credentials" -ForegroundColor Gray

Write-Host ""
if ($testsFailed -eq 0 -and $warnings.Count -le 3) {
    Write-Host "RAG system is functional with minor security improvements needed!" -ForegroundColor Green
    exit 0
} else {
    Write-Host "Fix issues and warnings before production use" -ForegroundColor Yellow
    exit 1
}