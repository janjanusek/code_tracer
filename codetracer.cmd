@echo off
rem CodeTracer launcher - run it with NO framework flag; it picks the runtime for you.
rem It (incrementally) builds every run - so it's always fresh after a `git pull` / edit (fast when
rem nothing changed) - then runs the net8.0 build, which auto-switches to the net472 build for
rem classic / .NET Framework / mixed solutions (see AutoRouter.cs).
rem   codetracer map -s Big.sln --method "Foo.Bar" --offline
setlocal
dotnet build "%~dp0CodeTracer.csproj" -c Debug --nologo -v quiet
if errorlevel 1 (
  echo [codetracer] build failed - fix the errors above and re-run.
  exit /b 1
)
dotnet "%~dp0bin\Debug\net8.0\CodeTracer.dll" %*
exit /b %ERRORLEVEL%
