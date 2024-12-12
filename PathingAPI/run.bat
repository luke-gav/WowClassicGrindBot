start "C:\Program Files (x86)\Google\Chrome\Application\chrome.exe" "http://192.168.0.19:5001"
cd /D "%~dp0"
dotnet run --configuration Release

pause