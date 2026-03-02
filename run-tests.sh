#!/bin/bash
# Cross-platform test runner script for Linux/macOS

# Parse command line arguments
COVERAGE=false
WATCH=false
FILTER=""
VERBOSE=false

while [[ $# -gt 0 ]]; do
  case $1 in
    --coverage)
      COVERAGE=true
      shift
      ;;
    --watch)
      WATCH=true
      shift
      ;;
    --filter)
      FILTER="$2"
      shift 2
      ;;
    --verbose)
      VERBOSE=true
      shift
      ;;
    *)
      echo "Unknown option: $1"
      echo "Usage: $0 [--coverage] [--watch] [--filter <pattern>] [--verbose]"
      exit 1
      ;;
  esac
done

echo "======================================"
echo "     CODE REVIEW AGENT - TEST RUNNER"
echo "======================================"
echo

# Check if .NET is installed
if ! command -v dotnet &> /dev/null; then
    echo "❌ .NET SDK is not installed or not in PATH"
    exit 1
fi

DOTNET_VERSION=$(dotnet --version)
echo "✅ .NET SDK Version: $DOTNET_VERSION"

# Build the test project first
echo "🔨 Building test project..."
dotnet build CodeReviewAgent.Tests.csproj --configuration Release --no-restore

if [ $? -ne 0 ]; then
    echo "❌ Build failed"
    exit 1
fi

echo "✅ Build successful"
echo

# Prepare test command
TEST_COMMAND="dotnet test CodeReviewAgent.Tests.csproj --configuration Release --no-build"

if [ "$VERBOSE" = true ]; then
    TEST_COMMAND="$TEST_COMMAND --verbosity detailed"
fi

if [ ! -z "$FILTER" ]; then
    TEST_COMMAND="$TEST_COMMAND --filter \"$FILTER\""
    echo "🔍 Running tests with filter: $FILTER"
fi

if [ "$COVERAGE" = true ]; then
    echo "📊 Running tests with coverage..."
    TEST_COMMAND="$TEST_COMMAND --collect:\"XPlat Code Coverage\" --results-directory TestResults"
    
    # Create TestResults directory if it doesn't exist
    mkdir -p TestResults
else
    echo "🧪 Running tests..."
fi

if [ "$WATCH" = true ]; then
    echo "👀 Running tests in watch mode..."
    TEST_COMMAND="${TEST_COMMAND/dotnet test/dotnet watch test}"
fi

echo "Command: $TEST_COMMAND"
echo

# Run tests
START_TIME=$(date +%s)
eval $TEST_COMMAND
EXIT_CODE=$?
END_TIME=$(date +%s)
DURATION=$((END_TIME - START_TIME))

echo
if [ $EXIT_CODE -eq 0 ]; then
    echo "✅ All tests passed!"
    echo "⏱️  Duration: ${DURATION} seconds"
    
    if [ "$COVERAGE" = true ]; then
        echo
        echo "📊 Coverage reports generated in TestResults/"
        
        # Try to find and display coverage summary
        COVERAGE_FILE=$(find TestResults -name "coverage.cobertura.xml" | head -1)
        if [ ! -z "$COVERAGE_FILE" ]; then
            echo "📁 Coverage file: $COVERAGE_FILE"
            
            # Try to install and use reportgenerator for HTML report
            if command -v reportgenerator &> /dev/null; then
                echo "📈 Generating HTML coverage report..."
                reportgenerator "-reports:$COVERAGE_FILE" "-targetdir:TestResults/CoverageReport" "-reporttypes:Html"
                echo "🌐 HTML report: TestResults/CoverageReport/index.html"
            else
                echo "💡 Install reportgenerator for HTML reports: dotnet tool install -g dotnet-reportgenerator-globaltool"
            fi
        fi
    fi
else
    echo "❌ Some tests failed!"
    echo "⏱️  Duration: ${DURATION} seconds"
    exit 1
fi

echo
echo "📋 Test Categories Available:"
echo "   - Unit Tests: $0 --filter \"Category=Unit\""
echo "   - Integration Tests: $0 --filter \"Category=Integration\""  
echo "   - Specific Test: $0 --filter \"FullyQualifiedName~RustReviewAgent\""
echo "   - With Coverage: $0 --coverage"
echo "   - Watch Mode: $0 --watch"