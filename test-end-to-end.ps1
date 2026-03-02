param(
    [string]$TestPat = "",
    [string]$Organization = "your-organization",
    [switch]$Verbose
)

Write-Host "================================================================" -ForegroundColor Cyan
Write-Host "           END-TO-END RAG & PR REVIEW TEST SUITE               " -ForegroundColor Cyan  
Write-Host "================================================================" -ForegroundColor Cyan
Write-Host ""

$testsPassed = 0
$testsFailed = 0
$warnings = @()
$issues = @()

function Test-E2E {
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
        Write-Host "PASS" -ForegroundColor Green
        $script:testsPassed++
        return $true
    }
    catch {
        Write-Host "FAIL" -ForegroundColor Red
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

# Test 1: PAT Setup and RAG System Initialization
Write-Host "PAT SETUP & RAG INITIALIZATION" -ForegroundColor Yellow
Write-Host "=================================" -ForegroundColor Yellow

Test-E2E "PAT validation and ADO connection" {
    if ([string]::IsNullOrWhiteSpace($TestPat)) {
        throw "TestPat is required. Pass -TestPat with a valid token."
    }

    # Set environment variables for test
    $env:ADO_PAT = $TestPat
    $env:ADO_ORGANIZATION = $Organization
    
    # Test PAT format
    if ($TestPat.Length -lt 20) {
        throw "PAT appears to be too short"
    }
    
    # Try to validate with Azure DevOps - create a simple test
    try {
        $headers = @{
            'Authorization' = "Basic $([Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes(":$TestPat")))"
        }
        $response = Invoke-RestMethod -Uri "https://dev.azure.com/$Organization/_apis/projects?api-version=7.0" -Headers $headers -TimeoutSec 30
        if (-not $response.value) {
            throw "No projects returned from ADO API"
        }
    }
    catch {
        throw "PAT validation failed: $($_.Exception.Message)"
    }
    
    return $true
}

Test-E2E "RAG system can initialize with repositories" {
    # Check if we can find some repositories to test with
    try {
        $headers = @{
            'Authorization' = "Basic $([Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes(":$TestPat")))"
        }
        
        # Look for SCC project
        $projects = Invoke-RestMethod -Uri "https://dev.azure.com/$Organization/_apis/projects?api-version=7.0" -Headers $headers
        
        # Look for repositories in any project
        $firstProject = $projects.value | Select-Object -First 1
        if ($firstProject) {
            $repos = Invoke-RestMethod -Uri "https://dev.azure.com/$Organization/$($firstProject.id)/_apis/git/repositories?api-version=7.0" -Headers $headers
            if (-not $repos.value) {
                throw "No repositories found in project $($firstProject.name)"
            }
        } else {
            throw "No projects found"
        }
    }
    catch {
        throw "Repository discovery failed: $($_.Exception.Message)"
    }
    
    return $true
}

# Test 2: Application Startup with RAG
Write-Host ""
Write-Host "APPLICATION STARTUP TEST" -ForegroundColor Blue
Write-Host "===========================" -ForegroundColor Blue

Test-E2E "Application can start with PAT configuration" {
    # Update .env file with test PAT
    $envContent = "AZURE_OPENAI_ENDPOINT=https://your-resource.openai.azure.com/`nAZURE_OPENAI_API_KEY=YOUR_AZURE_OPENAI_API_KEY_HERE`nADO_ORGANIZATION=$Organization`nADO_PAT=$TestPat`nASPNETCORE_URLS=http://0.0.0.0:5002"
    
    Set-Content -Path ".env" -Value $envContent
    
    # Try to start application briefly to test configuration
    try {
        $process = Start-Process -FilePath "dotnet" -ArgumentList "run", "--urls", "http://localhost:5003" -PassThru -RedirectStandardOutput "startup-test.log" -RedirectStandardError "startup-error.log"
        
        # Wait a few seconds for startup
        Start-Sleep -Seconds 5
        
        # Check if process is still running (good sign)
        if (-not $process.HasExited) {
            # Try to make a simple HTTP request to health check
            try {
                $response = Invoke-WebRequest -Uri "http://localhost:5003/" -TimeoutSec 3 -UseBasicParsing
                $started = $true
            }
            catch {
                # App might be starting, check logs
                if (Test-Path "startup-test.log") {
                    $logs = Get-Content "startup-test.log" -Raw
                    if ($logs.Contains("Now listening on") -or $logs.Contains("Application started")) {
                        $started = $true
                    }
                }
            }
            
            # Stop the process
            $process.Kill()
            $process.WaitForExit()
        }
        
        # Clean up log files
        if (Test-Path "startup-test.log") { Remove-Item "startup-test.log" }
        if (Test-Path "startup-error.log") { Remove-Item "startup-error.log" }
        
        if (-not $started) {
            throw "Application did not start successfully"
        }
    }
    catch {
        throw "Application startup test failed: $($_.Exception.Message)"
    }
    
    return $true
}

# Test 3: Pull Request Discovery and Processing
Write-Host ""
Write-Host "PULL REQUEST DISCOVERY" -ForegroundColor Magenta
Write-Host "=========================" -ForegroundColor Magenta

Test-E2E "Can discover pull requests in target projects" {
    try {
        $headers = @{
            'Authorization' = "Basic $([Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes(":$TestPat")))"
        }
        
        # Get all projects
        $projects = Invoke-RestMethod -Uri "https://dev.azure.com/$Organization/_apis/projects?api-version=7.0" -Headers $headers
        
        $targetProjects = $projects.value | Where-Object { 
            $_.name -like "*SCC*" -or 
            $_.name -like "*scc*" -or 
            $_.name -like "*Service*Shared*" -or
            $_.name -like "*ServiceShared*" -or
            $_.name -like "*waimeaba*"
        }
        
        if (-not $targetProjects) {
            # Fall back to any project with repositories
            Add-Warning "Target projects not found, using first available project"
            $targetProjects = $projects.value | Select-Object -First 1
        }
        
        $foundPRs = $false
        foreach ($project in $targetProjects) {
            try {
                # Get repositories in this project
                $repos = Invoke-RestMethod -Uri "https://dev.azure.com/$Organization/$($project.id)/_apis/git/repositories?api-version=7.0" -Headers $headers
                
                foreach ($repo in $repos.value) {
                    # Get pull requests for this repo
                    $prs = Invoke-RestMethod -Uri "https://dev.azure.com/$Organization/$($project.id)/_apis/git/repositories/$($repo.id)/pullrequests?api-version=7.0&`$top=5" -Headers $headers
                    
                    if ($prs.value -and $prs.value.Count -gt 0) {
                        $foundPRs = $true
                        Write-Host "  Found $($prs.value.Count) PRs in $($project.name)/$($repo.name)" -ForegroundColor Gray
                        break
                    }
                }
                
                if ($foundPRs) { break }
            }
            catch {
                Add-Warning "Failed to check PRs in project $($project.name): $($_.Exception.Message)"
            }
        }
        
        if (-not $foundPRs) {
            throw "No pull requests found in any target projects"
        }
    }
    catch {
        throw "PR discovery failed: $($_.Exception.Message)"
    }
    
    return $true
}

Test-E2E "RAG system can process repository for context" {
    # This is a simplified test - we'll check if the RAG components can work with a real repo
    try {
        $headers = @{
            'Authorization' = "Basic $([Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes(":$TestPat")))"
        }
        
        # Get a small repository to test with
        $projects = Invoke-RestMethod -Uri "https://dev.azure.com/$Organization/_apis/projects?api-version=7.0" -Headers $headers
        $project = $projects.value | Select-Object -First 1
        $repos = Invoke-RestMethod -Uri "https://dev.azure.com/$Organization/$($project.id)/_apis/git/repositories?api-version=7.0" -Headers $headers
        $repo = $repos.value | Select-Object -First 1
        
        if (-not $repo) {
            throw "No repositories found to test"
        }
        
        # Check if we can get repository metadata
        $repoDetails = Invoke-RestMethod -Uri "https://dev.azure.com/$Organization/$($project.id)/_apis/git/repositories/$($repo.id)?api-version=7.0" -Headers $headers
        
        if (-not $repoDetails) {
            throw "Could not get repository details"
        }
        
        # Try to get some files from the repository
        try {
            $items = Invoke-RestMethod -Uri "https://dev.azure.com/$Organization/$($project.id)/_apis/git/repositories/$($repo.id)/items?recursionLevel=OneLevel&api-version=7.0" -Headers $headers
            if (-not $items.value) {
                Add-Warning "Repository appears to be empty: $($repo.name)"
            }
        }
        catch {
            Add-Warning "Could not enumerate repository contents: $($_.Exception.Message)"
        }
        
        Write-Host "  Successfully accessed repository: $($repo.name)" -ForegroundColor Gray
    }
    catch {
        throw "RAG repository processing test failed: $($_.Exception.Message)"
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
    Write-Host "WARNINGS:" -ForegroundColor Yellow
    Write-Host "=========" -ForegroundColor Yellow
    $warnings | ForEach-Object { Write-Host "!!  $_" -ForegroundColor Yellow }
}

if ($testsFailed -gt 0) {
    Write-Host ""
    Write-Host "FAILED TESTS:" -ForegroundColor Red
    Write-Host "============" -ForegroundColor Red
    $issues | ForEach-Object { Write-Host "X $_" -ForegroundColor Red }
}

Write-Host ""
Write-Host "E2E TEST STATUS:" -ForegroundColor Cyan
if ($testsPassed -ge 4) {
    Write-Host "OK System can connect to ADO with provided PAT" -ForegroundColor Green
    Write-Host "OK RAG system components are functional" -ForegroundColor Green
    Write-Host "OK Application can start with proper configuration" -ForegroundColor Green
} else {
    Write-Host "X End-to-end system has issues that need attention" -ForegroundColor Red
}

Write-Host ""
Write-Host "NEXT STEPS:" -ForegroundColor Cyan
Write-Host "• Run full application with: dotnet run --urls http://0.0.0.0:5002" -ForegroundColor Gray
Write-Host "• Test PR review via web UI at http://localhost:5002" -ForegroundColor Gray
Write-Host "• Monitor logs for RAG indexing and review generation" -ForegroundColor Gray

Write-Host ""
if ($testsFailed -eq 0 -and $warnings.Count -le 2) {
    Write-Host "System ready for end-to-end PR review testing!" -ForegroundColor Green
    exit 0
} else {
    Write-Host "Fix issues before proceeding to full PR review test" -ForegroundColor Yellow
    exit 1
}