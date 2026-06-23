#!/usr/bin/env pwsh
# CodeTracer launcher (PowerShell / cross-platform) - run with NO framework flag; it picks the runtime.
#
# `dotnet run` can't auto-choose a framework on a multi-target project (it errors asking for
# --framework). This wrapper avoids that: it (incrementally) builds, then runs the net8.0 build, which
# auto-switches to the net472 build for classic / .NET Framework / mixed solutions (see AutoRouter.cs).
#
#   ./codetracer.ps1 map -s Big.sln --method "Foo.Bar" --offline
#
# It builds EVERY run (fast when nothing changed) so it's always fresh after a `git pull` / edit.
# NOTE: the tool writes [cfg]/[index] progress to stderr, which Windows PowerShell colours red - that
# is not an error. On Windows the codetracer.cmd launcher avoids the red noise.
$root = $PSScriptRoot

dotnet build (Join-Path $root 'CodeTracer.csproj') -c Debug --nologo -v quiet
if ($LASTEXITCODE -ne 0) {
    Write-Host '[codetracer] build failed - fix the errors above and re-run.' -ForegroundColor Red
    exit 1
}

& dotnet (Join-Path $root 'bin/Debug/net8.0/CodeTracer.dll') @args
exit $LASTEXITCODE
