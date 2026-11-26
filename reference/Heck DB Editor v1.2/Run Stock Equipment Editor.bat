@echo off
setlocal enabledelayedexpansion

title NHL Legacy - Stock Equipment Editor

echo.
echo  ============================================================
echo   NHL Legacy  ^|  Stock Team Equipment Editor
echo  ============================================================
echo.

set "CONFIG=%~dp0python_path.txt"
set "PYTHON="

:: ── Load saved path from config file ──────────────────────────────────────────
echo  [1/3] Locating Python...
if exist "%CONFIG%" (
    echo        Found saved Python path in config.
    for /f "usebackq delims=" %%P in ("%CONFIG%") do set "PYTHON=%%P"
)
if defined PYTHON (
    if exist "!PYTHON!" (
        echo        Using: !PYTHON!
        goto :install_deps
    )
    echo        Saved path no longer valid, searching for Python...
    set "PYTHON="
)

:: ── Try py launcher (Windows Python Launcher) ─────────────────────────────────
where py >nul 2>&1
if not errorlevel 1 (
    echo        Found Python via Windows Launcher ^(py^).
    set "PYTHON=py" & goto :install_deps
)

:: ── Try python on PATH ────────────────────────────────────────────────────────
where python >nul 2>&1
if not errorlevel 1 (
    echo        Found Python on system PATH.
    set "PYTHON=python" & goto :install_deps
)

:: ── Try common install locations ──────────────────────────────────────────────
for %%V in (313 312 311 310 39 38) do (
    if exist "%LocalAppData%\Programs\Python\Python%%V\python.exe" (
        set "PYTHON=%LocalAppData%\Programs\Python\Python%%V\python.exe"
        echo        Found Python%%V in LocalAppData.
        goto :install_deps
    )
    if exist "%ProgramFiles%\Python%%V\python.exe" (
        set "PYTHON=%ProgramFiles%\Python%%V\python.exe"
        echo        Found Python%%V in Program Files.
        goto :install_deps
    )
    if exist "%ProgramFiles(x86)%\Python%%V\python.exe" (
        set "PYTHON=%ProgramFiles(x86)%\Python%%V\python.exe"
        echo        Found Python%%V in Program Files (x86).
        goto :install_deps
    )
)

:: ── Not found — prompt user and save for next time ────────────────────────────
echo.
echo  Python was not found in any standard install location.
echo.
echo  Please enter the full path to your python.exe below.
echo  Example: C:\Users\YourName\AppData\Local\Programs\Python\Python312\python.exe
echo.
set /p PYTHON="  Python path: "

if not exist "%PYTHON%" (
    echo.
    echo  ERROR: File not found: %PYTHON%
    echo  Please install Python from https://www.python.org and re-run this script.
    echo.
    pause
    exit /b 1
)

:: Save the valid path so we don't ask again next time
>"%CONFIG%" echo !PYTHON!
echo.
echo  Path saved. Future launches will use this Python automatically.
echo.

:install_deps
echo.
echo  [2/3] Checking / installing required packages...
echo        Python: !PYTHON!
echo.

call :check_pkg Pillow                PIL
call :check_pkg numpy                 numpy
call :check_pkg PyOpenGL              OpenGL
call :check_pkg PyOpenGL_accelerate   OpenGL_accelerate
call :check_pkg pyopengltk            pyopengltk

echo.
echo  All packages ready -----------------------------------------
echo.

:run
echo  [3/3] Launching Stock Equipment Editor...
echo.
echo  ============================================================
echo.
"!PYTHON!" "%~dp0stock_team_equipment_editor.py"
if errorlevel 1 (
    echo.
    echo  The editor exited with an error. See above for details.
    pause
)

endlocal
goto :eof

:check_pkg
:: %1 = pip package name, %2 = import module name
echo    Checking %1 ...
"!PYTHON!" -c "import %2" >nul 2>&1
if errorlevel 1 (
    echo    Installing %1 ...
    "!PYTHON!" -m pip install %1 --quiet --disable-pip-version-check
    if errorlevel 1 (
        echo.
        echo    WARNING: Could not install %1. 3D preview may be unavailable.
        echo.
    ) else (
        echo    %1 installed successfully.
    )
) else (
    echo    %1 already installed.
)
exit /b 0
