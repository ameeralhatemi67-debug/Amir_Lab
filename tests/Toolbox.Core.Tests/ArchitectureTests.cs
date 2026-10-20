using System.Reflection;

namespace Toolbox.Core.Tests;

/// <summary>Guards the "local only" promise at the code level.</summary>
public class ArchitectureTests
{
    private static readonly string[] ForbiddenAssemblies =
    [
        "System.Net.Http",
        "System.Net.Requests",
        "System.Net.WebClient",
        "System.Net.Sockets",
        "System.Net.WebSockets.Client",
    ];

    public static IEnumerable<object[]> ProductAssemblies()
    {
        var dir = AppContext.BaseDirectory;
        foreach (var name in new[] { "Toolbox.Core.dll" })
            yield return [Path.Combine(dir, name)];

        // Sibling build outputs of the other product assemblies (same configuration).
        var root = FindRepoRoot(dir);
        if (root is null) yield break;
        foreach (var pattern in new[]
        {
            Path.Combine("src", "Toolbox.Processing", "bin"),
            Path.Combine("src", "Toolbox.Cli", "bin"),
            Path.Combine("src", "Toolbox.Windows", "bin"),
            Path.Combine("src", "Toolbox.Windows.Ocr", "bin"),
        })
        {
            var bin = Path.Combine(root, pattern);
            if (!Directory.Exists(bin)) continue;
            foreach (var file in Directory.EnumerateFiles(bin, "*.dll", SearchOption.AllDirectories)
                         .Concat(Directory.EnumerateFiles(bin, "*.exe", SearchOption.AllDirectories)))
            {
                var fileName = Path.GetFileName(file);
                if (fileName is "Toolbox.Processing.dll" or "ftb.dll" or "FileToolbox.dll" or "Toolbox.Windows.Ocr.dll")
                    yield return [file];
            }
        }
    }

    [Theory]
    [MemberData(nameof(ProductAssemblies))]
    public void Product_assemblies_do_not_reference_networking(string assemblyPath)
    {
        var references = AssemblyName.GetAssemblyName(assemblyPath) is not null
            ? LoadReferences(assemblyPath)
            : [];
        var offending = references.Where(r => ForbiddenAssemblies.Contains(r, StringComparer.OrdinalIgnoreCase)).ToList();
        Assert.True(offending.Count == 0, $"{Path.GetFileName(assemblyPath)} references {string.Join(", ", offending)}");
    }

    /// <summary>
    /// The Windows SDK projection exposes all of WinRT, including its HTTP and socket APIs; product
    /// code may only use the parts it needs (OCR, imaging, globalisation), never the networking ones.
    /// </summary>
    [Theory]
    [MemberData(nameof(ProductAssemblies))]
    public void Product_assemblies_do_not_use_WinRT_networking(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
        var md = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
        var offending = md.TypeReferences
            .Select(h => md.GetString(md.GetTypeReference(h).Namespace))
            .Where(ns => ns.StartsWith("Windows.Web", StringComparison.Ordinal) || ns.StartsWith("Windows.Networking", StringComparison.Ordinal))
            .Distinct()
            .ToList();
        Assert.True(offending.Count == 0, $"{Path.GetFileName(assemblyPath)} uses {string.Join(", ", offending)}");
    }

    private static List<string> LoadReferences(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
        var md = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
        return md.AssemblyReferences.Select(h => md.GetString(md.GetAssemblyReference(h).Name)).ToList();
    }

    private static string? FindRepoRoot(string start)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "FileToolbox.sln"))) return dir.FullName;
        return null;
    }
}
