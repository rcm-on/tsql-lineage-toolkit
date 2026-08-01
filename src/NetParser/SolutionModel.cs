using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace NetParser;

/// <summary>
/// Lightweight solution/project loader: parses .sln project entries and .csproj
/// XML (project + package references, source files) without MSBuild. Restore or
/// build of the target app is deliberately not required — the semantic model is
/// created from syntax trees plus the host runtime's reference assemblies, and
/// sink detection falls back to syntax when an external symbol cannot resolve.
/// </summary>
public sealed class SolutionInfo
{
    public required string Name { get; init; }
    public required string RootDir { get; init; }
    public List<ProjectInfo> Projects { get; } = new();
}

public sealed class ProjectInfo
{
    public required string Name { get; init; }
    public required string CsprojPath { get; init; }
    /// <summary>web | test | console | library, inferred from Sdk/OutputType/test packages.</summary>
    public string Kind { get; set; } = "library";
    public List<string> ProjectReferences { get; } = new();       // project names
    public List<(string Name, string Version)> PackageReferences { get; } = new();
    public List<string> SourceFiles { get; } = new();             // absolute paths
    /// <summary>From appsettings*.json ConnectionStrings: name + Database/Initial Catalog when present.</summary>
    public List<(string Name, string? Database)> ConnectionStrings { get; } = new();
}

public static class SolutionLoader
{
    private static readonly Regex SlnProject = new(
        "Project\\(\"\\{[^}]+\\}\"\\)\\s*=\\s*\"(?<name>[^\"]+)\",\\s*\"(?<path>[^\"]+\\.csproj)\"",
        RegexOptions.IgnoreCase);

    public static SolutionInfo Load(string inputPath)
    {
        inputPath = Path.GetFullPath(inputPath);

        if (inputPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
            return FromSln(inputPath);

        if (inputPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            var sln = new SolutionInfo
            {
                Name = Path.GetFileNameWithoutExtension(inputPath),
                RootDir = Path.GetDirectoryName(inputPath)!,
            };
            sln.Projects.Add(LoadProject(inputPath));
            return sln;
        }

        // Directory: prefer a .sln at (or under) it, else take every .csproj.
        var slnFile = Directory.EnumerateFiles(inputPath, "*.sln", SearchOption.AllDirectories)
            .OrderBy(p => p.Length).FirstOrDefault();
        if (slnFile is not null)
            return FromSln(slnFile);

        var result = new SolutionInfo { Name = Path.GetFileName(inputPath.TrimEnd('\\', '/')), RootDir = inputPath };
        foreach (var csproj in Directory.EnumerateFiles(inputPath, "*.csproj", SearchOption.AllDirectories))
            result.Projects.Add(LoadProject(csproj));
        return result;
    }

    private static SolutionInfo FromSln(string slnPath)
    {
        var dir = Path.GetDirectoryName(slnPath)!;
        var sln = new SolutionInfo { Name = Path.GetFileNameWithoutExtension(slnPath), RootDir = dir };
        foreach (Match m in SlnProject.Matches(File.ReadAllText(slnPath)))
        {
            var rel = m.Groups["path"].Value.Replace('\\', Path.DirectorySeparatorChar);
            var full = Path.GetFullPath(Path.Combine(dir, rel));
            if (File.Exists(full))
                sln.Projects.Add(LoadProject(full));
        }
        return sln;
    }

    private static ProjectInfo LoadProject(string csprojPath)
    {
        var info = new ProjectInfo
        {
            Name = Path.GetFileNameWithoutExtension(csprojPath),
            CsprojPath = csprojPath,
        };

        var doc = XDocument.Load(csprojPath);
        foreach (var el in doc.Descendants())
        {
            if (el.Name.LocalName == "ProjectReference" && el.Attribute("Include")?.Value is { } prRel)
                info.ProjectReferences.Add(Path.GetFileNameWithoutExtension(prRel.Replace('\\', '/')));
            else if (el.Name.LocalName == "PackageReference" && el.Attribute("Include")?.Value is { } pkg)
                info.PackageReferences.Add((pkg, el.Attribute("Version")?.Value ?? ""));
        }

        string sdk = doc.Root?.Attribute("Sdk")?.Value ?? "";
        string outputType = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "OutputType")?.Value ?? "";
        bool isTest = info.PackageReferences.Any(p =>
            p.Name.Contains("xunit", StringComparison.OrdinalIgnoreCase) ||
            p.Name.Contains("NUnit", StringComparison.OrdinalIgnoreCase) ||
            p.Name.Contains("MSTest", StringComparison.OrdinalIgnoreCase));
        info.Kind = sdk.Contains("Web", StringComparison.OrdinalIgnoreCase) ? "web"
            : isTest ? "test"
            : outputType.Equals("Exe", StringComparison.OrdinalIgnoreCase) ? "console"
            : "library";

        var projDir = Path.GetDirectoryName(csprojPath)!;
        foreach (var cs in Directory.EnumerateFiles(projDir, "*.cs", SearchOption.AllDirectories))
        {
            var relToProj = Path.GetRelativePath(projDir, cs);
            if (relToProj.StartsWith("bin" + Path.DirectorySeparatorChar) ||
                relToProj.StartsWith("obj" + Path.DirectorySeparatorChar))
                continue;
            info.SourceFiles.Add(cs);
        }

        foreach (var settings in Directory.EnumerateFiles(projDir, "appsettings*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                using var settingsDoc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(settings));
                if (settingsDoc.RootElement.TryGetProperty("ConnectionStrings", out var cs) &&
                    cs.ValueKind == System.Text.Json.JsonValueKind.Object)
                    foreach (var entry in cs.EnumerateObject())
                    {
                        var raw = entry.Value.GetString() ?? "";
                        var db = Regex.Match(raw, @"(?:Database|Initial Catalog)\s*=\s*(?<db>[^;]+)", RegexOptions.IgnoreCase);
                        if (!info.ConnectionStrings.Any(c => c.Name == entry.Name))
                            info.ConnectionStrings.Add((entry.Name, db.Success ? db.Groups["db"].Value.Trim() : null));
                    }
            }
            catch (System.Text.Json.JsonException) { /* malformed settings file: skip */ }
        }
        return info;
    }

    /// <summary>Projects in dependency order (referenced projects first).</summary>
    public static List<ProjectInfo> TopoSort(SolutionInfo sln)
    {
        var byName = sln.Projects.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var order = new List<ProjectInfo>();

        void Visit(ProjectInfo p)
        {
            if (!done.Add(p.Name)) return;
            foreach (var dep in p.ProjectReferences)
                if (byName.TryGetValue(dep, out var d))
                    Visit(d);
            order.Add(p);
        }

        foreach (var p in sln.Projects) Visit(p);
        return order;
    }
}
