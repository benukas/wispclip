@echo off
setlocal EnableExtensions EnableDelayedExpansion

cd /d "%~dp0"
set "ROOT=%CD%"
set "TOOLS=%ROOT%\tools"
set "DIST=%ROOT%\dist\Wispclip"
set "PROJECT=%ROOT%\src\Wispclip\Wispclip.csproj"

rem Detect a double-click launch so we can keep the window open at the end.
set "LAUNCHED_FROM_EXPLORER=0"
echo %cmdcmdline% | findstr /i /c:"%~nx0" >nul 2>&1 && set "LAUNCHED_FROM_EXPLORER=1"

if /i "%~1"=="help" goto :help
if /i "%~1"=="--help" goto :help
if /i "%~1"=="/?" goto :help
if /i "%~1"=="deps" goto :deps_only
if /i "%~1"=="build" goto :build_only
if /i "%~1"=="clean" goto :clean
if not "%~1"=="" (
    echo Unknown option: %~1
    echo.
    goto :help
)

call :banner
call :check_dotnet || goto :fail
call :ensure_ffmpeg || goto :fail
call :publish || goto :fail
call :stage_tools || goto :fail
call :summary
goto :end

:deps_only
call :banner
call :check_dotnet || goto :fail
call :ensure_ffmpeg || goto :fail
echo.
echo Dependencies are ready.
goto :end

:build_only
call :banner
call :check_dotnet || goto :fail
if not exist "%TOOLS%\ffmpeg.exe" (
    echo [ERROR] ffmpeg not found in tools\
    echo Run configure.cmd deps first, or run configure.cmd with no arguments.
    goto :fail
)
call :publish || goto :fail
call :stage_tools || goto :fail
call :summary
goto :end

:clean
echo Removing build output...
if exist "%ROOT%\dist\Wispclip" rmdir /s /q "%ROOT%\dist\Wispclip"
for /d /r "%ROOT%\src" %%D in (bin,obj) do if exist "%%D" rmdir /s /q "%%D"
echo Done.
goto :end

:help
echo Wispclip configurator
echo.
echo Usage:
echo   configure.cmd          Check dependencies, build, and stage the release
echo   configure.cmd deps     Check dependencies only
echo   configure.cmd build    Build and stage dist\Wispclip ^(requires deps^)
echo   configure.cmd clean    Remove dist, bin, and obj folders
echo   configure.cmd help     Show this help
echo.
echo Requirements:
echo   - Windows 10 or 11
echo   - .NET 8 SDK  ^(https://dotnet.microsoft.com/download/dotnet/8.0^)
echo   - FFmpeg      ^(https://ffmpeg.org/download.html^) - download it yourself and
echo                 place ffmpeg.exe and ffprobe.exe in tools\. Wispclip does not
echo                 bundle or download FFmpeg automatically.
goto :end

:banner
echo.
echo  Wispclip configurator
echo  =====================
echo.
exit /b 0

:check_dotnet
echo [1/4] Checking .NET 8 SDK...
where dotnet >nul 2>&1
if errorlevel 1 (
    echo [ERROR] dotnet was not found on PATH.
    echo Install the .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0
    echo Or run: winget install Microsoft.DotNet.SDK.8
    exit /b 1
)

set "HAS_NET8="
for /f "delims=" %%L in ('dotnet --list-sdks 2^>nul') do (
    echo %%L | findstr /R /C:"^8\." >nul && set "HAS_NET8=1"
)
if not defined HAS_NET8 (
    echo [ERROR] A .NET 8 SDK was not found.
    dotnet --list-sdks 2>nul
    echo.
    echo Install the .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0
    exit /b 1
)

for /f "delims=" %%V in ('dotnet --version 2^>nul') do set "DOTNET_VER=%%V"
echo       OK - dotnet !DOTNET_VER!
exit /b 0

:ensure_ffmpeg
echo [2/4] Checking FFmpeg...
if exist "%TOOLS%\ffmpeg.exe" if exist "%TOOLS%\ffprobe.exe" (
    echo       OK - found in tools\
    exit /b 0
)

echo [ERROR] FFmpeg not found in tools\
echo.
echo Wispclip does not bundle or auto-download FFmpeg. Get a Windows build
echo yourself from the official page:
echo.
echo     https://ffmpeg.org/download.html
echo.
echo Use a build that includes the ddagrab filter, which Wispclip needs for
echo GPU screen capture ^(the essentials build from gyan.dev, linked from the
echo page above, works^). Extract the archive, then copy these two files into:
echo.
echo     %TOOLS%\
echo.
echo         ffmpeg.exe
echo         ffprobe.exe
echo.
echo Then run configure.cmd again.
exit /b 1

:publish
echo [3/4] Building Wispclip...

rem A running Wispclip.exe locks the single-file output, which fails the publish with an
rem IOException. Closing it first (best-effort - fine if it wasn't running) avoids that.
rem Try a graceful close first so the app can shut down its capture/tray state cleanly;
rem fall back to a force-kill if it's still around after a couple seconds.
tasklist /FI "IMAGENAME eq Wispclip.exe" 2>nul | findstr /I "Wispclip.exe" >nul
if not errorlevel 1 (
    echo       Closing running Wispclip.exe...
    taskkill /IM Wispclip.exe /T >nul 2>&1
    timeout /t 2 /nobreak >nul
    tasklist /FI "IMAGENAME eq Wispclip.exe" 2>nul | findstr /I "Wispclip.exe" >nul
    if not errorlevel 1 taskkill /F /IM Wispclip.exe /T >nul 2>&1
)

rem ReadyToRun pre-compiles the app to native code so cold startup (especially at Windows
rem sign-in) skips most JIT work, at the cost of a somewhat larger executable.
dotnet publish "%PROJECT%" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishReadyToRun=true -p:DebugType=None -p:DebugSymbols=false -o "%DIST%"
if errorlevel 1 (
    echo [ERROR] Build failed.
    exit /b 1
)
echo       OK - output in dist\Wispclip\
exit /b 0

:stage_tools
echo [4/4] Staging release folder...
if not exist "%DIST%\tools" mkdir "%DIST%\tools"
copy /Y "%TOOLS%\ffmpeg.exe" "%DIST%\tools\" >nul
copy /Y "%TOOLS%\ffprobe.exe" "%DIST%\tools\" >nul
if not exist "%DIST%\tools\ffmpeg.exe" (
    echo [ERROR] Failed to copy FFmpeg into dist\Wispclip\tools\
    exit /b 1
)
echo       OK - dist\Wispclip\ is ready to zip
exit /b 0

:summary
echo.
echo  Done.
echo  -----
echo  Run:  dist\Wispclip\Wispclip.exe
echo.
echo  Release folder:  dist\Wispclip\
echo  Zip that folder for GitHub Releases.
echo.
exit /b 0

:fail
echo.
echo Configuration did not complete.
if "%LAUNCHED_FROM_EXPLORER%"=="1" (
    echo.
    pause
)
exit /b 1

:end
if "%LAUNCHED_FROM_EXPLORER%"=="1" (
    echo.
    pause
)
exit /b 0
