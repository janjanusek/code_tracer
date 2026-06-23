using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace CodeTracer;

/// <summary>
/// Picks the right runtime automatically so the user never has to think about .NET versions.
///
/// MSBuildWorkspace can only host ONE MSBuild family, decided by THIS process's runtime:
///   - on .NET (Core/8) it gets the SDK MSBuild  -> loads SDK-style projects only;
///   - on .NET Framework it gets the full VS MSBuild -> loads BOTH classic (non-SDK / packages.config)
///     AND modern SDK-style projects.
/// A process cannot switch families mid-flight. So if we're on Core and the target solution contains
/// classic (non-SDK) projects, we transparently re-exec the sibling net472 build with the same args.
///
/// IMPORTANT: this type must not touch any MSBuild/Roslyn type - it runs BEFORE MSBuildLocator.
/// </summary>
internal static class AutoRouter
{
    /// Returns a process exit code if it re-launched the other build (caller should return it),
    /// or null to keep running in the current process.
    public static int? Route(string[] args)
    {
        // Already on .NET Framework? The full VS MSBuild loads everything - nothing to route.
        if (RuntimeInformation.FrameworkDescription.StartsWith(".NET Framework", StringComparison.OrdinalIgnoreCase))
            return null;

        var sln = FindSolutionArg(args);
        if (sln is null || !File.Exists(sln)) return null;     // no/!found solution -> let normal flow handle it

        if (!SolutionHasClassicProject(sln)) return null;       // SDK-only -> the SDK MSBuild (here) is fine

        // Legacy/mixed solution + we're on Core -> hand over to the Framework build.
        var exe = FindFrameworkBuild();
        if (exe is null)
        {
            Console.Error.WriteLine(
                "[auto] this solution has classic (non-SDK) .NET Framework project(s), which the .NET (Core) " +
                "MSBuild cannot load. Build the Framework target once:  dotnet build -f net472  " +
                "(then re-run the same command). Continuing on the .NET build - legacy projects may not load.");
            return null;
        }

        try
        {
            Console.Error.WriteLine(
                $"[auto] classic (non-SDK) .NET Framework project(s) detected -> switching to the .NET Framework " +
                $"build for full MSBuild:\n       {exe}");
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,           // inherit console: stdin/out/err + exit code
                Arguments = QuoteArgs(args),        // ArgumentList is .NET Core only; build the string ourselves
            };
            using var child = Process.Start(psi)!;
            child.WaitForExit();
            return child.ExitCode;
        }
        catch (Exception ex)
        {
            // e.g. no .NET Framework runtime here (non-Windows) - fall back to running in-process.
            Console.Error.WriteLine($"[auto] could not start the Framework build ({ex.Message}); " +
                                    "continuing on the .NET build - legacy projects may not load.");
            return null;
        }
    }

    /// Pull the solution path from -s/--solution (everything else is irrelevant to routing).
    private static string? FindSolutionArg(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] is "-s" or "--solution")
                return args[i + 1];
        return null;
    }

    /// True if ANY project referenced by the .sln is a classic (non-SDK-style) project - i.e. it
    /// needs the full Framework MSBuild. .slnx (new XML format) is skipped (parsed elsewhere).
    private static bool SolutionHasClassicProject(string slnPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(slnPath)) ?? "";
            var text = File.ReadAllText(slnPath);
            // Project(...) = "Name", "relative\Project.csproj", "{GUID}"
            foreach (Match m in Regex.Matches(text, "\"([^\"]+\\.csproj)\"", RegexOptions.IgnoreCase))
            {
                var rel = m.Groups[1].Value.Replace('\\', Path.DirectorySeparatorChar);
                var csproj = Path.GetFullPath(Path.Combine(dir, rel));
                if (File.Exists(csproj) && !IsSdkStyle(csproj))
                    return true;
            }
        }
        catch { /* unreadable sln -> don't route, let the normal loader report it */ }
        return false;
    }

    /// SDK-style projects declare an Sdk (e.g. <Project Sdk="Microsoft.NET.Sdk">); classic ones don't.
    private static bool IsSdkStyle(string csprojPath)
    {
        try
        {
            var head = File.ReadAllText(csprojPath);
            return Regex.IsMatch(head, "<Project[^>]*\\bSdk\\s*=", RegexOptions.IgnoreCase)
                || head.IndexOf("Sdk=\"Microsoft.NET.Sdk", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch { return true; }   // can't read -> assume modern, don't force a route
    }

    /// Quote argv into a single Windows command line (CommandLineToArgvW rules), so the relaunched
    /// process re-parses the exact same arguments - paths with spaces, --question "…", etc.
    private static string QuoteArgs(string[] args) => string.Join(" ", args.Select(QuoteArg));

    private static string QuoteArg(string a)
    {
        if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return a;
        var sb = new StringBuilder("\"");
        int slashes = 0;
        foreach (var c in a)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') { sb.Append('\\', slashes * 2 + 1).Append('"'); }
            else { sb.Append('\\', slashes).Append(c); }
            slashes = 0;
        }
        sb.Append('\\', slashes * 2).Append('"');
        return sb.ToString();
    }

    /// Locate the sibling net472 executable next to the current build (…/bin/<cfg>/net8.0 -> …/net472).
    /// Override with the CODETRACER_NET472 env var for non-standard layouts.
    private static string? FindFrameworkBuild()
    {
        var env = Environment.GetEnvironmentVariable("CODETRACER_NET472");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;

        var baseDir = AppContext.BaseDirectory;                  // …\bin\Debug\net8.0\
        foreach (var candidate in new[]
                 {
                     Path.Combine(baseDir, "..", "net472", "CodeTracer.exe"),
                     Path.Combine(baseDir, "..", "..", "net472", "CodeTracer.exe"),
                 })
        {
            var full = Path.GetFullPath(candidate);
            if (File.Exists(full)) return full;
        }
        return null;
    }
}
