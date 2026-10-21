@echo off
setlocal
cd /d "%~dp0"

set "EXE=src\Voicemet.App\bin\Debug\net10.0-windows\Voicemet.App.exe"

if not exist "%EXE%" (
    echo Building Voicemet...
    dotnet build "src\Voicemet.App\Voicemet.App.csproj" -c Debug
    if errorlevel 1 (
        echo.
        echo Build failed. Make sure the .NET 10 SDK is installed.
        pause
        exit /b 1
    )
)

start "" "%EXE%"
