@echo off
echo ================================================
echo PS5 Upload Client - Building
echo By Manos
echo ================================================
echo.

echo [+] Building Windows GUI Client...
dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true

if exist "bin\Release\net6.0-windows\win-x64\publish\PS5Upload.exe" (
    echo.
    echo ================================================
    echo [+] SUCCESS! Built PS5Upload.exe
    echo ================================================
    echo.
    echo Location: bin\Release\net6.0-windows\win-x64\publish\PS5Upload.exe
    echo.
    echo Copy PS5Upload.exe to any location and run it!
) else (
    echo.
    echo ================================================
    echo [-] BUILD FAILED
    echo ================================================
    echo.
    echo Make sure .NET 6.0 SDK is installed:
    echo https://dotnet.microsoft.com/download/dotnet/6.0
    exit /b 1
)
