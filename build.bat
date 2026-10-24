@echo off
rem Builds GrannyMP.dll into <GameDir>\BepInEx\plugins\GrannyMP (GameDir: GrannyMP.local.props)
cd /d "%~dp0"
dotnet build GrannyMP.csproj -c Release
pause
