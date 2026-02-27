#!/bin/bash
# Bash script to monitor runtime.log in real-time
# Usage: ./watch-logs.sh

echo "🔍 Code Review Agent - Runtime Log Monitor"
echo "═══════════════════════════════════════════"
echo ""

LOG_FILE="runtime.log"

if [ ! -f "$LOG_FILE" ]; then
    echo "⚠️  Log file '$LOG_FILE' not found."
    echo "   Make sure the Code Review Agent is running."
    echo ""
    echo "💡 Start the agent with:"
    echo "   dotnet run --web"
    echo ""
    exit 1
fi

echo "📁 Monitoring: $(pwd)/$LOG_FILE"
echo "🔄 Press Ctrl+C to stop monitoring"
echo ""

# Show existing content first
echo "📖 Current log content:"
echo "───────────────────────"
cat "$LOG_FILE"
echo ""
echo "🔴 LIVE LOG (new entries will appear below):"
echo "─────────────────────────────────────────────"

# Start monitoring new content
tail -f "$LOG_FILE"