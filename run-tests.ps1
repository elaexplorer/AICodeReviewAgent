# PowerShell script to run all tests with coverage and reporting
param(
    [switch]$Coverage,
    [switch]$Watch,
    [string]$Filter = "",
    [switch]$Verbose
)

Write-Host "======================================" -ForegroundColor Cyan
Write-Host "     CODE REVIEW AGENT - TEST RUNNER" -ForegroundColor Cyan  
Write-Host "======================================" -ForegroundColor Cyan
Write-Host ""

# Check if .NET is installed
if (-not (Get-Command "dotnet" -ErrorAction SilentlyContinue)) {
    Write-Host "❌ .NET SDK is not installed or not in PATH" -ForegroundColor Red
    exit 1
}

$dotnetVersion = dotnet --version
Write-Host "✅ .NET SDK Version: $dotnetVersion" -ForegroundColor Green

# Build the test project first
Write-Host "🔨 Building test project..." -ForegroundColor Yellow
dotnet build CodeReviewAgent.Tests.csproj --configuration Release --no-restore

if ($LASTEXITCODE -ne 0) {
    Write-Host "❌ Build failed" -ForegroundColor Red
    exit 1
}

Write-Host "✅ Build successful" -ForegroundColor Green
Write-Host ""

# Prepare test command
$testCommand = "dotnet test CodeReviewAgent.Tests.csproj --configuration Release --no-build"

if ($Verbose) {
    $testCommand += " --verbosity detailed"
}

if ($Filter -ne "") {
    $testCommand += " --filter `"$Filter`""
    Write-Host "🔍 Running tests with filter: $Filter" -ForegroundColor Yellow
}

if ($Coverage) {
    Write-Host "📊 Running tests with coverage..." -ForegroundColor Yellow
    $testCommand += " --collect:`"XPlat Code Coverage`" --results-directory TestResults"
    
    # Create TestResults directory if it doesn't exist
    if (-not (Test-Path "TestResults")) {
        New-Item -ItemType Directory -Name "TestResults" | Out-Null
    }
} else {
    Write-Host "🧪 Running tests..." -ForegroundColor Yellow
}

if ($Watch) {
    Write-Host "👀 Running tests in watch mode..." -ForegroundColor Yellow
    $testCommand = $testCommand.Replace("dotnet test", "dotnet watch test")
}

Write-Host "Command: $testCommand" -ForegroundColor Gray
Write-Host ""

# Run tests - Use simple TestRunner if xUnit packages aren't available
$startTime = Get-Date

if (Test-Path "CodeReviewAgent.Tests.csproj") {
    Write-Host "Using xUnit test framework..." -ForegroundColor Gray
    Invoke-Expression $testCommand
} else {
    Write-Host "Using built-in TestRunner..." -ForegroundColor Gray
    dotnet run --project . -- --test-runner
}

$endTime = Get-Date
$duration = $endTime - $startTime

Write-Host ""
if ($LASTEXITCODE -eq 0) {
    Write-Host "✅ All tests passed!" -ForegroundColor Green
    Write-Host "⏱️  Duration: $($duration.TotalSeconds.ToString('F2')) seconds" -ForegroundColor Gray
    
    if ($Coverage) {
        Write-Host ""
        Write-Host "📊 Coverage reports generated in TestResults/" -ForegroundColor Cyan
        
        # Try to find and display coverage summary
        $coverageFiles = Get-ChildItem -Path "TestResults" -Filter "coverage.cobertura.xml" -Recurse
        if ($coverageFiles.Count -gt 0) {
            Write-Host "📁 Coverage file: $($coverageFiles[0].FullName)" -ForegroundColor Gray
            
            # Try to install and use reportgenerator for HTML report
            if (Get-Command "reportgenerator" -ErrorAction SilentlyContinue) {
                Write-Host "📈 Generating HTML coverage report..." -ForegroundColor Yellow
                reportgenerator "-reports:$($coverageFiles[0].FullName)" "-targetdir:TestResults/CoverageReport" "-reporttypes:Html"
                Write-Host "🌐 HTML report: TestResults/CoverageReport/index.html" -ForegroundColor Cyan
            } else {
                Write-Host "💡 Install reportgenerator for HTML reports: dotnet tool install -g dotnet-reportgenerator-globaltool" -ForegroundColor Yellow
            }
        }
    }
} else {
    Write-Host "❌ Some tests failed!" -ForegroundColor Red
    Write-Host "⏱️  Duration: $($duration.TotalSeconds.ToString('F2')) seconds" -ForegroundColor Gray
    exit 1
}

Write-Host ""
Write-Host "📋 Test Categories Available:" -ForegroundColor Cyan
Write-Host "   - Unit Tests: run-tests.ps1 -Filter `"Category=Unit`"" -ForegroundColor Gray
Write-Host "   - Integration Tests: run-tests.ps1 -Filter `"Category=Integration`"" -ForegroundColor Gray  
Write-Host "   - Specific Test: run-tests.ps1 -Filter `"FullyQualifiedName~RustReviewAgent`"" -ForegroundColor Gray
Write-Host "   - With Coverage: run-tests.ps1 -Coverage" -ForegroundColor Gray
Write-Host "   - Watch Mode: run-tests.ps1 -Watch" -ForegroundColor Gray