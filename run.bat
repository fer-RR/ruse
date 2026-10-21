@echo off
setlocal
cd /d "%~dp0"

set "EXE=src\Ruse.App\bin\Debug\net10.0-windows\Ruse.exe"

if not exist "%EXE%" (
    echo Building Ruse...
    dotnet build "src\Ruse.App\Ruse.App.csproj" -c Debug
    if errorlevel 1 (
        echo.
        echo Build failed. Make sure the .NET 10 SDK is installed.
        pause
        exit /b 1
    )
)

start "" "%EXE%"
