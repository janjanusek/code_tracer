@echo off
rem CodeTracer launcher - run it with NO framework flag; it picks the runtime for you.
rem Runs the net8.0 build, which auto-switches to the net472 build for classic / .NET Framework
rem / mixed solutions (see AutoRouter.cs). One command, no --framework.
rem   codetracer map -s Big.sln --method "Foo.Bar" --offline
setlocal
set "DLL=%~dp0bin\Debug\net8.0\CodeTracer.dll"
if not exist "%DLL%" (
  echo [codetracer] first run - building ^(net8.0 + net472^)...
  dotnet build "%~dp0CodeTracer.csproj" -c Debug
)
dotnet "%DLL%" %*
exit /b %ERRORLEVEL%
