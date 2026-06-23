using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
#if NETFRAMEWORK
using System.Net.Http;
#endif

namespace CodeTracer;

/// <summary>
/// Compatibility helpers so ONE source tree compiles+runs on net8.0 AND net472. Modern-BCL methods
/// missing on .NET Framework are provided here; on net8.0 each helper just forwards to the BCL.
/// Language features (Index, Range, init, required, nullable attrs) are polyfilled by PolySharp
/// (see CodeTracer.csproj) - that part needs no code here.
/// </summary>
internal static class Compat
{
    /// File.WriteAllTextAsync is .NET Core only.
    public static Task WriteAllTextAsync(string path, string contents)
    {
#if NETFRAMEWORK
        File.WriteAllText(path, contents);
        return Task.CompletedTask;
#else
        return File.WriteAllTextAsync(path, contents);
#endif
    }

    /// Path.GetRelativePath is .NET Core only; net472 falls back to a Uri-based computation.
    public static string GetRelativePath(string relativeTo, string path)
    {
#if NETFRAMEWORK
        if (string.IsNullOrEmpty(relativeTo)) return path;
        var sep = Path.DirectorySeparatorChar;
        var baseDir = relativeTo.EndsWith(sep.ToString()) || relativeTo.EndsWith(Path.AltDirectorySeparatorChar.ToString())
            ? relativeTo : relativeTo + sep;
        try
        {
            var baseUri = new Uri(Path.GetFullPath(baseDir));
            var targetUri = new Uri(Path.GetFullPath(path));
            if (baseUri.Scheme != targetUri.Scheme) return path;             // e.g. different drive
            var rel = Uri.UnescapeDataString(baseUri.MakeRelativeUri(targetUri).ToString());
            return rel.Replace('/', sep);
        }
        catch { return path; }
#else
        return Path.GetRelativePath(relativeTo, path);
#endif
    }

    /// Split on a single char, trim each entry, drop empties - TFM-agnostic. The char overload
    /// Split(char, StringSplitOptions) and StringSplitOptions.TrimEntries are both .NET Core only.
    public static string[] SplitClean(this string s, char sep)
        => s.Split(new[] { sep }, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToArray();
}

#if NETFRAMEWORK
internal static class NetFrameworkShims
{
    /// HttpContent.ReadAsStringAsync(CancellationToken) arrived in .NET 5; ignore the token on net472.
    public static Task<string> ReadAsStringAsync(this HttpContent content, CancellationToken cancellationToken)
        => content.ReadAsStringAsync();

    /// IDictionary&lt;,&gt;.TryAdd is .NET Core only.
    public static bool TryAdd<TKey, TValue>(this IDictionary<TKey, TValue> dict, TKey key, TValue value)
    {
        if (dict.ContainsKey(key)) return false;
        dict.Add(key, value);
        return true;
    }
}
#endif
