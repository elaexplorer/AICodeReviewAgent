@echo off
cd /d "C:\Users\elavarasid\workspace\mscs\CodeReviewAIAgent"
"C:\Python311\python.exe" scripts\daily_pr_review.py --hours 24 >> "%USERPROFILE%\daily_pr_review.log" 2>&1
