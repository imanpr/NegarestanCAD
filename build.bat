@echo off
chcp 65001 >nul
echo ============================================================
echo   NegarestanCAD - One-Click Windows Build Script (.NET 8)
echo ============================================================
echo.

where dotnet >nul 2>nul
if %errorlevel% neq 0 (
    echo [ERROR] .NET 8 SDK is not installed or not in PATH!
    echo Please install .NET 8 SDK or Visual Studio 2022 (.NET Desktop Development).
    pause
    exit /b 1
)

if not exist "src\NegarestanCAD\Fonts\Vazirmatn-Regular.ttf" (
    echo [INFO] Vazirmatn font not found in Fonts folder. Downloading automatically...
    call download_vazirmatn.bat
)

echo.
echo [1/2] Restoring NuGet packages (ACadSharp, ClosedXML)...
dotnet restore src\NegarestanCAD\NegarestanCAD.csproj
if %errorlevel% neq 0 (
    echo [ERROR] NuGet restore failed. Check your internet connection.
    pause
    exit /b 1
)

echo.
echo [2/2] Building standalone single-file Windows EXE (win-x64)...
dotnet publish src\NegarestanCAD\NegarestanCAD.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish\win-x64
if %errorlevel% neq 0 (
    echo [ERROR] Build failed!
    pause
    exit /b 1
)

echo.
echo ============================================================
echo   BUILD SUCCEEDED!
echo   EXE Location: publish\win-x64\NegarestanCAD.exe
echo ============================================================
echo.
pause
