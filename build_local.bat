@echo off
chcp 65001 > nul
cd /d %~dp0
REM cd /d ..

echo ========================================
echo   BetterGI Local Build Script
echo ========================================
echo.

REM Check prerequisites
dotnet --version >nul 2>&1
if errorlevel 1 (
    echo Error: .NET SDK not found, please install .NET 8 SDK
    pause
    exit /b 1
)

git --version >nul 2>&1
if errorlevel 1 (
    echo Error: Git not found, please install Git
    pause
    exit /b 1
)

REM Run PowerShell script embedded
powershell -ExecutionPolicy Bypass -Command ^
    "$ProjectRoot = Get-Location;" ^
    "$TmpDir = Join-Path $ProjectRoot 'Tmp';" ^
    "$DistDir = Join-Path $TmpDir 'dist';" ^
    "$OutputDir = Join-Path $DistDir 'BetterGI';" ^
    "" ^
    "Write-Host 'Reading version...' -ForegroundColor Yellow;" ^
    "$csprojPath = Join-Path $ProjectRoot 'BetterGenshinImpact\BetterGenshinImpact.csproj';" ^
    "$csprojContent = Get-Content $csprojPath -Raw;" ^
    "if ($csprojContent -match '<Version>([^<]+)</Version>') { $Version = $matches[1] } else { $Version = '0.0.0-dev' };" ^
    "Write-Host \"Version: $Version\";" ^
    "" ^
    "Write-Host 'Preparing directories...' -ForegroundColor Yellow;" ^
    "if (-not (Test-Path $TmpDir)) { New-Item -ItemType Directory -Path $TmpDir -Force | Out-Null };" ^
    "if (Test-Path $DistDir) { Remove-Item -Recurse -Force $DistDir };" ^
    "New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null;" ^
    "" ^
    "Write-Host '';" ^
    "Write-Host '[1/3] Building Web Resources...' -ForegroundColor Green;" ^
    "$nodeExists = Get-Command node -ErrorAction SilentlyContinue;" ^
    "if (-not $nodeExists) {" ^
    "    Write-Host 'Warning: Node.js not found, skipping web resources' -ForegroundColor Yellow" ^
    "} else {" ^
    "    $mapDir = Join-Path $TmpDir 'bettergi-map';" ^
    "    $scriptWebDir = Join-Path $TmpDir 'bettergi-script-web';" ^
    "" ^
    "    if (Test-Path $mapDir) {" ^
    "        Write-Host '  -> Updating bettergi-map...';" ^
    "        Push-Location $mapDir; git fetch origin; $branch = git symbolic-ref refs/remotes/origin/HEAD | ForEach-Object { $_ -replace 'refs/remotes/origin/', '' }; git checkout $branch; git reset --hard origin/$branch; Pop-Location;" ^
    "    } else {" ^
    "        Write-Host '  -> Cloning bettergi-map...';" ^
    "        git clone https://github.com/huiyadanli/bettergi-map.git $mapDir;" ^
    "    };" ^
    "    Push-Location $mapDir; npm install; npm run build:single; Pop-Location;" ^
    "" ^
    "    if (Test-Path $scriptWebDir) {" ^
    "        Write-Host '  -> Updating bettergi-script-web...';" ^
    "        Push-Location $scriptWebDir; git fetch origin; $branch = git symbolic-ref refs/remotes/origin/HEAD | ForEach-Object { $_ -replace 'refs/remotes/origin/', '' }; git checkout $branch; git reset --hard origin/$branch; Pop-Location;" ^
    "    } else {" ^
    "        Write-Host '  -> Cloning bettergi-script-web...';" ^
    "        git clone https://github.com/zaodonganqi/bettergi-script-web.git $scriptWebDir;" ^
    "    };" ^
    "    Push-Location $scriptWebDir; npm install; npm run build:single; Pop-Location;" ^
    "" ^
    "    $mapEditorDir = Join-Path $OutputDir 'Assets\Map\Editor';" ^
    "    $scriptRepoDir = Join-Path $OutputDir 'Assets\Web\ScriptRepo';" ^
    "    New-Item -ItemType Directory -Path $mapEditorDir -Force | Out-Null;" ^
    "    New-Item -ItemType Directory -Path $scriptRepoDir -Force | Out-Null;" ^
    "    Copy-Item -Path \"$mapDir\dist\*\" -Destination $mapEditorDir -Recurse -Force;" ^
    "    Copy-Item -Path \"$scriptWebDir\dist\*\" -Destination $scriptRepoDir -Recurse -Force;" ^
    "    Write-Host '  -> Web resources completed';" ^
    "}" ^
    "" ^
    "Write-Host '';" ^
    "Write-Host '[2/3] Building Main Application...' -ForegroundColor Green;" ^
    "$CsprojPath = Join-Path $ProjectRoot 'BetterGenshinImpact\BetterGenshinImpact.csproj';" ^
    "$PublishDir = Join-Path $ProjectRoot 'BetterGenshinImpact\bin\x64\Release\net8.0-windows10.0.22621.0\publish\win-x64';" ^
    "if (Test-Path $PublishDir) { Remove-Item -Recurse -Force $PublishDir };" ^
    "dotnet publish $CsprojPath -c Release -p:PublishProfile=FolderProfile -p:Version=$Version;" ^
    "if ($LASTEXITCODE -ne 0) { Write-Host 'dotnet publish failed!' -ForegroundColor Red; exit 1 };" ^
    "Copy-Item -Path \"$PublishDir\*\" -Destination $OutputDir -Recurse -Force;" ^
    "Get-ChildItem -Path $OutputDir -Recurse -Filter '*.lib' | Remove-Item -Force;" ^
    "Get-ChildItem -Path $OutputDir -Recurse -Filter '*ffmpeg*.dll' | Remove-Item -Force;" ^
    "Get-ChildItem -Path $OutputDir -Recurse -Filter '*.pdb' | Remove-Item -Force;" ^
    "Write-Host '  -> Main application completed';" ^
    "" ^
    "Write-Host '';" ^
    "Write-Host '[3/3] Packaging...' -ForegroundColor Green;" ^
    "$sevenZip = Join-Path $ProjectRoot 'Build\MicaSetup.Tools\7-Zip\7z.exe';" ^
    "$ArchiveFile = Join-Path $DistDir \"BetterGI_v$Version.7z\";" ^
    "if (Test-Path $ArchiveFile) { Remove-Item -Force $ArchiveFile };" ^
    "Push-Location $DistDir; & $sevenZip a \"BetterGI_v$Version.7z\" 'BetterGI' -t7z -mx=5 -r -y; Pop-Location;" ^
    "Remove-Item -Recurse -Force $OutputDir;" ^
    "Write-Host \"  -^> Created: $ArchiveFile\";" ^
    "" ^
    "Write-Host '';" ^
    "Write-Host '========================================' -ForegroundColor Cyan;" ^
    "Write-Host '  Build Completed!' -ForegroundColor Green;" ^
    "Write-Host '========================================' -ForegroundColor Cyan;" ^
    "Write-Host '';" ^
    "Write-Host \"Output: $ArchiveFile\";"

if errorlevel 1 (
    echo.
    echo Build failed with error code: %errorlevel%
    pause
    exit /b 1
)

echo.
pause
