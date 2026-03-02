# Automated test runner that runs on every code update
# This script can be integrated with file watchers or CI/CD pipelines

param(
    [switch]$Quick,      # Run only fast tests
    [switch]$Full,       # Run all tests including slow integration tests
    [switch]$PreCommit   # Run pre-commit test suite
)

function Write-Banner {
    param([string]$Message, [string]$Color = "Cyan")
    Write-Host ""
    Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor $Color
    Write-Host " $Message" -ForegroundColor $Color
    Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor $Color
    Write-Host ""
}

Write-Banner "AUTOMATED CODE REVIEW AGENT TESTING"

# Check if any source files have changed
$sourceFiles = Get-ChildItem -Path . -Include @("*.cs", "*.csproj") -Recurse | Where-Object { $_.LastWriteTime -gt (Get-Date).AddMinutes(-10) }
if ($sourceFiles.Count -eq 0 -and -not $Full -and -not $PreCommit) {
    Write-Host "ℹ️  No recent source file changes detected in the last 10 minutes" -ForegroundColor Yellow
    Write-Host "   Use -Full to run all tests anyway" -ForegroundColor Yellow
    exit 0
}

Write-Host "📝 Recent changes detected in:" -ForegroundColor Yellow
$sourceFiles | ForEach-Object { Write-Host "   - $($_.Name)" -ForegroundColor Gray }
Write-Host ""

$allTestsPassed = $true
$startTime = Get-Date

try {
    if ($Quick -or $PreCommit) {
        Write-Banner "QUICK TESTS (Unit Tests Only)" "Green"
        
        # Run only fast unit tests
        & .\run-tests.ps1 -Filter "Category=Unit|FullyQualifiedName!~Integration" -Verbose
        if ($LASTEXITCODE -ne 0) { $allTestsPassed = $false }
        
        # Quick build verification
        Write-Host "🔨 Verifying build..." -ForegroundColor Yellow
        dotnet build --configuration Release --verbosity quiet
        if ($LASTEXITCODE -ne 0) { 
            Write-Host "❌ Build verification failed" -ForegroundColor Red
            $allTestsPassed = $false 
        } else {
            Write-Host "✅ Build verification passed" -ForegroundColor Green
        }
    }

    if ($Full -or (-not $Quick -and -not $PreCommit)) {
        Write-Banner "COMPREHENSIVE TEST SUITE" "Blue"
        
        # 1. Unit Tests
        Write-Host "🧪 Running Unit Tests..." -ForegroundColor Yellow
        & .\run-tests.ps1 -Filter "Category=Unit" -Coverage -Verbose
        if ($LASTEXITCODE -ne 0) { $allTestsPassed = $false }
        
        # 2. Integration Tests  
        Write-Host "🔗 Running Integration Tests..." -ForegroundColor Yellow
        & .\run-tests.ps1 -Filter "Category=Integration" -Verbose
        if ($LASTEXITCODE -ne 0) { $allTestsPassed = $false }
        
        # 3. Performance Tests
        Write-Host "⚡ Running Performance Tests..." -ForegroundColor Yellow
        & .\run-tests.ps1 -Filter "Category=Performance" -Verbose
        if ($LASTEXITCODE -ne 0) { $allTestsPassed = $false }
        
        # 4. Security Scan
        Write-Banner "SECURITY SCAN" "Magenta"
        Write-Host "🔒 Checking for vulnerable packages..." -ForegroundColor Yellow
        $vulnerablePackages = dotnet list package --vulnerable --include-transitive 2>&1
        if ($vulnerablePackages -match "has the following vulnerable packages") {
            Write-Host "❌ Vulnerable packages found:" -ForegroundColor Red
            Write-Host $vulnerablePackages -ForegroundColor Red
            $allTestsPassed = $false
        } else {
            Write-Host "✅ No vulnerable packages found" -ForegroundColor Green
        }
        
        # 5. Code Style Check (if tools are available)
        if (Get-Command "dotnet-format" -ErrorAction SilentlyContinue) {
            Write-Host "📐 Checking code formatting..." -ForegroundColor Yellow
            dotnet format --verify-no-changes --verbosity quiet
            if ($LASTEXITCODE -ne 0) {
                Write-Host "⚠️  Code formatting issues detected. Run 'dotnet format' to fix." -ForegroundColor Yellow
            } else {
                Write-Host "✅ Code formatting is correct" -ForegroundColor Green
            }
        }
    }

    # Pre-commit specific checks
    if ($PreCommit) {
        Write-Banner "PRE-COMMIT CHECKS" "Yellow"
        
        # Check for debug statements, TODO comments in new code
        Write-Host "🔍 Checking for debug statements..." -ForegroundColor Yellow
        $debugStatements = Select-String -Path "*.cs" -Pattern "(Console\.WriteLine|Debug\.|TODO|FIXME|HACK)" -Recurse
        if ($debugStatements.Count -gt 0) {
            Write-Host "⚠️  Found debug statements or TODO comments:" -ForegroundColor Yellow
            $debugStatements | ForEach-Object { Write-Host "   $($_.Filename):$($_.LineNumber) - $($_.Line.Trim())" -ForegroundColor Gray }
        }
        
        # Check for large files
        $largeFiles = Get-ChildItem -Recurse -File | Where-Object { $_.Length -gt 1MB }
        if ($largeFiles.Count -gt 0) {
            Write-Host "⚠️  Large files detected (>1MB):" -ForegroundColor Yellow
            $largeFiles | ForEach-Object { Write-Host "   $($_.Name) - $([math]::Round($_.Length/1MB, 2))MB" -ForegroundColor Gray }
        }
    }

} catch {
    Write-Host "❌ Test execution failed with error: $($_.Exception.Message)" -ForegroundColor Red
    $allTestsPassed = $false
}

$endTime = Get-Date
$duration = $endTime - $startTime

Write-Banner "TEST RESULTS SUMMARY"

if ($allTestsPassed) {
    Write-Host "✅ ALL TESTS PASSED!" -ForegroundColor Green
    Write-Host "🎉 Code is ready for deployment/commit" -ForegroundColor Green
} else {
    Write-Host "❌ SOME TESTS FAILED!" -ForegroundColor Red
    Write-Host "🛑 Fix issues before committing/deploying" -ForegroundColor Red
}

Write-Host "⏱️  Total duration: $($duration.TotalMinutes.ToString('F1')) minutes" -ForegroundColor Gray
Write-Host "📊 Test results and coverage reports available in TestResults/" -ForegroundColor Cyan

if ($allTestsPassed) {
    Write-Host ""
    Write-Host "🚀 Next steps:" -ForegroundColor Cyan
    Write-Host "   - Review coverage report: TestResults/CoverageReport/index.html" -ForegroundColor Gray
    Write-Host "   - Commit changes: git add . && git commit -m 'Your message'" -ForegroundColor Gray
    Write-Host "   - Deploy: Your deployment process" -ForegroundColor Gray
} else {
    Write-Host ""
    Write-Host "🔧 To fix issues:" -ForegroundColor Cyan
    Write-Host "   - Check test output above for specific failures" -ForegroundColor Gray
    Write-Host "   - Run individual tests: .\run-tests.ps1 -Filter 'TestName'" -ForegroundColor Gray
    Write-Host "   - Fix code and re-run: .\test-on-update.ps1" -ForegroundColor Gray
}

exit $(if ($allTestsPassed) { 0 } else { 1 })