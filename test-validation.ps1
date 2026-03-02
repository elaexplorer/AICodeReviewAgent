param(
    [switch]$Verbose
)

Write-Host "Code Review Agent - Validation Tests" -ForegroundColor Cyan
Write-Host "====================================" -ForegroundColor Cyan
Write-Host ""

$passed = 0
$failed = 0
$warnings = 0

function Test-Item {
    param(
        [string]$Name,
        [scriptblock]$Test
    )
    
    Write-Host "Testing $Name... " -NoNewline
    try {
        $result = & $Test
        if ($result -eq $false) {
            throw "Test returned false"
        }
        Write-Host "PASS" -ForegroundColor Green
        $script:passed++
        return $true
    }
    catch {
        Write-Host "FAIL" -ForegroundColor Red
        if ($Verbose) {
            Write-Host "  Error: $($_.Exception.Message)" -ForegroundColor Red
        }
        $script:failed++
        return $false
    }
}

# Test 1: Required files exist
Test-Item "Core project files" {
    $files = @("CodeReviewAgent.csproj", "Program.cs", ".env")
    foreach ($file in $files) {
        if (-not (Test-Path $file)) {
            throw "Missing file: $file"
        }
    }
    return $true
}

Test-Item "Service layer files" {
    $files = @(
        "Services/CodeReviewService.cs",
        "Services/AzureDevOpsMcpClient.cs",
        "Services/CodebaseContextService.cs"
    )
    foreach ($file in $files) {
        if (-not (Test-Path $file)) {
            throw "Missing service file: $file"
        }
    }
    return $true
}

Test-Item "Agent files" {
    $files = @(
        "Agents/RustReviewAgent.cs",
        "Agents/PythonReviewAgent.cs", 
        "Agents/DotNetReviewAgent.cs"
    )
    foreach ($file in $files) {
        if (-not (Test-Path $file)) {
            throw "Missing agent file: $file"
        }
    }
    return $true
}

Test-Item "Test infrastructure" {
    $files = @(
        "TestRunner.cs",
        "Tests/TestFixtures/MockPullRequestData.cs",
        "Tests/TestFixtures/MockAIService.cs"
    )
    foreach ($file in $files) {
        if (-not (Test-Path $file)) {
            throw "Missing test file: $file"
        }
    }
    return $true
}

# Test 2: Configuration validation
Test-Item "Environment configuration" {
    if (-not (Test-Path ".env")) {
        throw ".env file missing"
    }
    
    $envContent = Get-Content ".env" -Raw -ErrorAction SilentlyContinue
    if ($envContent -and $envContent.Contains("AZURE_OPENAI_ENDPOINT")) {
        return $true
    }
    throw ".env missing required configuration"
}

# Test 3: Git optimization verification
Test-Item "Git clone optimization" {
    $contextFile = "Services/CodebaseContextService.cs"
    if (Test-Path $contextFile) {
        $content = Get-Content $contextFile -Raw
        
        # Check that problematic filter is removed
        if ($content.Contains("--filter=blob:none")) {
            throw "Still using problematic --filter=blob:none option"
        }
        
        # Check that timeout is reasonable
        if ($content.Contains("120")) {
            $script:warnings++
            Write-Host ""
            Write-Host "  WARNING: Found 120 second timeout - consider increasing" -ForegroundColor Yellow
        }
        
        return $true
    }
    throw "CodebaseContextService.cs not found"
}

# Test 4: Test runner availability
Test-Item "Test runner scripts" {
    $files = @("run-tests.ps1", "run-tests.sh", "test-on-update.ps1")
    foreach ($file in $files) {
        if (-not (Test-Path $file)) {
            throw "Missing test script: $file"
        }
    }
    return $true
}

# Summary
Write-Host ""
Write-Host "Test Summary" -ForegroundColor Cyan
Write-Host "============" -ForegroundColor Cyan
Write-Host "Passed:   $passed" -ForegroundColor Green
Write-Host "Failed:   $failed" -ForegroundColor $(if ($failed -gt 0) { "Red" } else { "Green" })
Write-Host "Warnings: $warnings" -ForegroundColor Yellow

if ($failed -eq 0) {
    Write-Host ""
    Write-Host "All validation tests passed!" -ForegroundColor Green
    Write-Host "Code Review Agent structure is verified." -ForegroundColor Green
    exit 0
}
else {
    Write-Host ""
    Write-Host "Some validation tests failed." -ForegroundColor Red
    Write-Host "Please fix issues before deploying." -ForegroundColor Red
    exit 1
}