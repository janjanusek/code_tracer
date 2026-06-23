#!/usr/bin/env pwsh
# CodeTracer launcher (PowerShell / cross-platform) - run with NO framework flag; it picks the runtime.
#
# `dotnet run` can't auto-choose a framework on a multi-target project (it errors asking for
# --framework). This wrapper avoids that: it runs the net8.0 build, which then auto-switches to the
# net472 build for classic / .NET Framework / mixed solutions (see AutoRouter.cs). One command.
#
#   ./codetracer.ps1 map -s Big.sln --method "Foo.Bar" --offline
#
# NOTE on Windows PowerShell: the tool writes its [cfg]/[index] progress to stderr, which Win PS
# colours red - that's not an error. On Windows the codetracer.cmd launcher avoids the red noise.
$root = $PSScriptRoot
$dll  = Join-Path $root 'bin/Debug/net8.0/CodeTracer.dll'

if (-not (Test-Path $dll)) {
    Write-Host '[codetracer] first run - building (net8.0 + net472)...' -ForegroundColor Cyan
    dotnet build (Join-Path $root 'CodeTracer.csproj') -c Debug
}

& dotnet $dll @args
exit $LASTEXITCODE
