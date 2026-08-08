using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace NetParser;

/// <summary>
/// Lightweight solution/project loader: parses .sln project entries and .csproj
/// XML (project + package references, source files) without MSBuild. Restore or
/// build of the target app is deliberately not required — the semantic model is
/// created from syntax trees plus the host runtime's reference assemblies, and
/// sink detection falls back to syntax when an external symbol cannot resolve.
///
/// C# only: .vbproj/.fsproj entries in a .sln are skipped. That is a scope decision,
/// not a bug — but it does mean a mixed-language solution is reported partially.
/// </summary>
public sealed class SolutionInfo
{
    public required string Name { get; init; }
    public required string RootDir { get; init; }
    public List<ProjectInfo> Projects { get; } = new();
    /// <summary>
    /// Projects the extractor will not analyse. Never silently dropped: analysing part
    /// of a solution and presenting it as the whole produces confident wrong answers,
    /// which is worse than refusing. See UnsupportedProjectException.
    /// </summary>
    public List<UnsupportedProject> Unsupported { get; } = new();
}

/// <param name="Reason">One line, addressed to whoever ran the tool.</param>
public sealed record UnsupportedProject(string Name, string Path, string Reason);

/// <summary>
/// Thrown when the input contains projects outside what NetParser can extract. The
/// message states which ones, why, and what the product does not cover — so the limit
/// is read once here instead of being inferred later from a graph with holes.
/// </summary>
public sealed class UnsupportedProjectException : Exception
{
    public IReadOnlyList<UnsupportedProject> Projects { get; }

    public UnsupportedProjectException(IReadOnlyList<UnsupportedProject> projects)
        : base(BuildMessage(projects))
        => Projects = projects;

    private static string BuildMessage(IReadOnlyList<UnsupportedProject> projects)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Refusing to extract: {projects.Count} project(s) are outside what this parser covers.");
        foreach (var p in projects)
            sb.AppendLine($"  - {p.Name} ({p.Path}): {p.Reason}");
        sb.AppendLine();
        sb.AppendLine("Product limits, by decision and not by omission:");
        sb.AppendLine("  - C# only. VB.NET and F# projects are not parsed.");
        sb.AppendLine("  - Server-side code only: no desktop UI (WinForms, WPF, MAUI) and no WebForms.");
        sb.AppendLine("    Their entry points are events and page lifecycles, which this parser does not model,");
        sb.AppendLine("    so any SQL reached through them would be missing from the impact graph.");
        sb.AppendLine();
        sb.AppendLine("Analysing the rest anyway would report a partial graph as if it were complete.");
        sb.AppendLine("Pass --allow-partial to proceed: excluded projects are then written into the graph");
        sb.AppendLine("as nodes with analyzed=false, so the hole travels with the data.");
        return sb.ToString();
    }
}

public sealed class ProjectInfo
{
    public required string Name { get; init; }
    public required string CsprojPath { get; init; }
    /// <summary>
    /// web | worker | function | console | test | library, inferred from Sdk/OutputType
    /// and packages. Not only web APIs: services, batch processes and class libraries
    /// reach the same database. Desktop UI (WinForms/WPF/MAUI) and WebForms are out of
    /// scope by decision, so they are not classified rather than half-supported.
    /// </summary>
    public string Kind { get; set; } = "library";
    public List<string> ProjectReferences { get; } = new();       // project names
    public List<(string Name, string Version)> PackageReferences { get; } = new();
    /// <summary>Binary assembly references with a HintPath: in-house DLLs shipped without sources.</summary>
    public List<string> AssemblyReferences { get; } = new();
    public List<string> SourceFiles { get; } = new();             // absolute paths
    /// <summary>
    /// Connection strings from appsettings*.json (SDK-style) and App/Web.config
    /// (.NET Framework): name + Database/Initial Catalog when present.
    /// </summary>
    public List<(string Name, string? Database)> ConnectionStrings { get; } = new();
}

public static class SolutionLoader
{
    // Every project entry, not just the C# ones: a .vbproj that goes unnoticed is a
    // hole in the graph that nobody is told about.
    private static readonly Regex SlnProject = new(
        "Project\\(\"\\{[^}]+\\}\"\\)\\s*=\\s*\"(?<name>[^\"]+)\",\\s*\"(?<path>[^\"]+\\.(?:cs|vb|fs)proj)\"",
        RegexOptions.IgnoreCase);

    public static SolutionInfo Load(string inputPath)
    {
        inputPath = Path.GetFullPath(inputPath);

        if (inputPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
            return FromSln(inputPath);

        if (inputPath.EndsWith("proj", StringComparison.OrdinalIgnoreCase))
        {
            var sln = new SolutionInfo
            {
                Name = Path.GetFileNameWithoutExtension(inputPath),
                RootDir = Path.GetDirectoryName(inputPath)!,
            };
            Admit(sln, inputPath);
            return sln;
        }

        // Directory: prefer a .sln at (or under) it, else take every .csproj.
        var slnFile = Directory.EnumerateFiles(inputPath, "*.sln", SearchOption.AllDirectories)
            .OrderBy(p => p.Length).FirstOrDefault();
        if (slnFile is not null)
            return FromSln(slnFile);

        var result = new SolutionInfo { Name = Path.GetFileName(inputPath.TrimEnd('\\', '/')), RootDir = inputPath };
        foreach (var proj in Directory.EnumerateFiles(inputPath, "*.*proj", SearchOption.AllDirectories)
                     .Where(p => p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
                                 p.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase) ||
                                 p.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            Admit(result, proj);
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
                Admit(sln, full);
        }
        return sln;
    }

    /// <summary>Loads the project, or records why it will not be analysed.</summary>
    private static void Admit(SolutionInfo sln, string projectPath)
    {
        string name = Path.GetFileNameWithoutExtension(projectPath);
        string rel = Path.GetRelativePath(sln.RootDir, projectPath).Replace('\\', '/');

        if (!projectPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            string language = projectPath.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase) ? "VB.NET" : "F#";
            sln.Unsupported.Add(new UnsupportedProject(name, rel, $"written in {language}; this parser reads C# only"));
            return;
        }

        var project = LoadProject(projectPath);
        if (UnsupportedReason(projectPath) is { } reason)
        {
            sln.Unsupported.Add(new UnsupportedProject(name, rel, reason));
            return;
        }
        sln.Projects.Add(project);
    }

    /// <summary>
    /// Out-of-scope project shapes. Detected on purpose rather than classified: giving
    /// a WinForms project a label would suggest its event handlers are in the graph.
    /// </summary>
    private static string? UnsupportedReason(string csprojPath)
    {
        XDocument doc;
        try { doc = XDocument.Load(csprojPath); }
        catch (System.Xml.XmlException) { return null; }

        string Prop(string name) =>
            doc.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value ?? "";

        if (Prop("UseWindowsForms").Equals("true", StringComparison.OrdinalIgnoreCase))
            return "WinForms project; desktop UI entry points are not modelled";
        if (Prop("UseWPF").Equals("true", StringComparison.OrdinalIgnoreCase) ||
            doc.Descendants().Any(e => e.Name.LocalName is "ApplicationDefinition" or "Page"))
            return "WPF project; desktop UI entry points are not modelled";
        if (Prop("UseMaui").Equals("true", StringComparison.OrdinalIgnoreCase))
            return "MAUI project; desktop/mobile UI entry points are not modelled";
        if (doc.Descendants().Any(e => e.Name.LocalName == "Reference" &&
                                       (e.Attribute("Include")?.Value ?? "")
                                       .StartsWith("System.Windows.Forms", StringComparison.OrdinalIgnoreCase)))
            return "references System.Windows.Forms; desktop UI entry points are not modelled";

        var projDir = Path.GetDirectoryName(csprojPath)!;
        if (Directory.EnumerateFiles(projDir, "*.aspx", SearchOption.AllDirectories).Any() ||
            Directory.EnumerateFiles(projDir, "*.ascx", SearchOption.AllDirectories).Any())
            return "WebForms project (.aspx/.ascx); page lifecycle entry points are not modelled";

        return null;
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
            // Binary assembly reference (<Reference Include="Foo" HintPath="..\lib\Foo.dll">):
            // the in-house DLL with no sources. Its dependency is architecture even
            // when its body is out of reach.
            else if (el.Name.LocalName == "Reference" && el.Attribute("Include")?.Value is { } asmRef &&
                     el.Elements().Any(c => c.Name.LocalName == "HintPath"))
                info.AssemblyReferences.Add(asmRef.Split(',')[0].Trim());
        }

        // packages.config: the .NET Framework way of declaring dependencies, still
        // the norm in the legacy estates where the stored procedures live.
        var packagesConfig = Path.Combine(Path.GetDirectoryName(csprojPath)!, "packages.config");
        if (File.Exists(packagesConfig))
        {
            try
            {
                foreach (var pkg in XDocument.Load(packagesConfig).Descendants()
                             .Where(e => e.Name.LocalName == "package"))
                    if (pkg.Attribute("id")?.Value is { } id &&
                        !info.PackageReferences.Any(p => p.Name == id))
                        info.PackageReferences.Add((id, pkg.Attribute("version")?.Value ?? ""));
            }
            catch (System.Xml.XmlException) { /* malformed packages.config: skip */ }
        }

        info.Kind = InferKind(doc, info);

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
                        AddConnectionString(info, entry.Name, entry.Value.GetString() ?? "");
            }
            catch (System.Text.Json.JsonException) { /* malformed settings file: skip */ }
        }

        // App.config / Web.config: where every pre-SDK project keeps its connection
        // strings. Without this a WinForms or WCF app looks like it talks to no database.
        foreach (var name in new[] { "App.config", "app.config", "Web.config", "web.config" })
        {
            var configPath = Path.Combine(projDir, name);
            if (!File.Exists(configPath)) continue;
            try
            {
                foreach (var add in XDocument.Load(configPath).Descendants()
                             .Where(e => e.Name.LocalName == "add" &&
                                         e.Parent?.Name.LocalName == "connectionStrings"))
                    if (add.Attribute("name")?.Value is { } csName)
                        AddConnectionString(info, csName, add.Attribute("connectionString")?.Value ?? "");
            }
            catch (System.Xml.XmlException) { /* malformed config: skip */ }
        }
        return info;
    }

    private static void AddConnectionString(ProjectInfo info, string name, string raw)
    {
        var db = Regex.Match(raw, @"(?:Database|Initial Catalog)\s*=\s*(?<db>[^;]+)", RegexOptions.IgnoreCase);
        if (!info.ConnectionStrings.Any(c => c.Name == name))
            info.ConnectionStrings.Add((name, db.Success ? db.Groups["db"].Value.Trim() : null));
    }

    /// <summary>
    /// Project type from whatever the file format offers: the Sdk attribute and package
    /// list in SDK-style projects, OutputType and packages in pre-SDK ones (which is all
    /// a .NET Framework service or batch job leaves behind).
    /// </summary>
    private static string InferKind(XDocument doc, ProjectInfo info)
    {
        string sdk = doc.Root?.Attribute("Sdk")?.Value ?? "";
        string Prop(string name) =>
            doc.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value ?? "";

        bool HasPackage(string fragment) => info.PackageReferences.Any(p =>
            p.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase));

        if (info.PackageReferences.Any(p =>
                p.Name.Contains("xunit", StringComparison.OrdinalIgnoreCase) ||
                p.Name.Contains("NUnit", StringComparison.OrdinalIgnoreCase) ||
                p.Name.Contains("MSTest", StringComparison.OrdinalIgnoreCase)))
            return "test";

        if (sdk.Contains("Razor", StringComparison.OrdinalIgnoreCase) ||
            sdk.Contains("Web", StringComparison.OrdinalIgnoreCase) ||
            HasPackage("Microsoft.AspNet.WebApi") || HasPackage("Microsoft.AspNet.Mvc")) return "web";
        if (HasPackage("Microsoft.NET.Sdk.Functions") || HasPackage("Microsoft.Azure.Functions")) return "function";
        if (HasPackage("Microsoft.Extensions.Hosting.WindowsServices") ||
            HasPackage("Microsoft.Extensions.Hosting.Systemd") ||
            HasPackage("Topshelf")) return "worker";

        // WinExe means a desktop UI, which is out of scope: it stays "library" so no
        // consumer reads a classification the extractor cannot back up.
        return Prop("OutputType").Equals("Exe", StringComparison.OrdinalIgnoreCase) ? "console" : "library";
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
