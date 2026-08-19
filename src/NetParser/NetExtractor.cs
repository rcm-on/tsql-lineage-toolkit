using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Parser.Contracts;

namespace NetParser;

/// <summary>
/// Roslyn-based extractor for .NET solutions/projects. Emits, in Vocab terms:
///   structure  - AppSolution/AppProject/AppPackage/AppFile/AppClass/AppMethod,
///                CONTAINS, DEPENDS_ON, CALLS (semantic, intra-solution). Calls
///                through an interface also get a resolved CALLS to the member
///                that runs (props via=interface, resolution=di|unique_impl), so
///                the chain controller -> service -> repository -> SQL closes.
///   sql bridge - EXECUTES_SQL (AppMethod -> SqlObject|Table) from string flow
///                into ADO/Dapper/EF sinks; confidence EXTRACTED (literal),
///                RESOLVED (call-site narrowing), AMBIGUOUS (catalog template
///                candidates, off the impact path by default)
///   ef         - MAPS_TO (entity AppClass -> Table), READS_FROM/WRITES_TO
///                (AppMethod -> Table) from DbSet usage
///   rules      - Rule nodes for conditionals governing a SQL call site,
///                (Rule)-[:GOVERNS]->(AppMethod), mirroring the SQL side
/// The target app is parsed, never restored/built: symbols that resolve are
/// used (locals, consts, own types, intra-solution calls); external sinks are
/// recognized by syntax. See docs/task-app-bridge.md.
/// </summary>
public class NetExtractor : IGraphExtractor
{
    private static readonly HashSet<string> DapperMethods = new(StringComparer.Ordinal)
    {
        "Query", "QueryAsync", "QueryFirst", "QueryFirstAsync", "QueryFirstOrDefault", "QueryFirstOrDefaultAsync",
        "QuerySingle", "QuerySingleAsync", "QuerySingleOrDefault", "QuerySingleOrDefaultAsync", "QueryMultiple",
        "QueryMultipleAsync", "Execute", "ExecuteAsync", "ExecuteScalar", "ExecuteScalarAsync",
    };

    private static readonly HashSet<string> EfRawMethods = new(StringComparer.Ordinal)
    {
        "SqlQueryRaw", "SqlQuery", "FromSqlRaw", "FromSqlInterpolated", "FromSql",
        "ExecuteSqlRaw", "ExecuteSqlRawAsync", "ExecuteSqlInterpolated", "ExecuteSqlInterpolatedAsync",
        "ExecuteSql", "ExecuteSqlAsync",
    };

    private static readonly HashSet<string> HttpClientMethods = new(StringComparer.Ordinal)
    {
        "GetAsync", "GetStringAsync", "GetFromJsonAsync", "GetByteArrayAsync", "GetStreamAsync",
        "PostAsync", "PostAsJsonAsync", "PutAsync", "PutAsJsonAsync", "PatchAsync", "DeleteAsync",
    };

    private static readonly HashSet<string> EfWriteMethods = new(StringComparer.Ordinal)
    {
        "Add", "AddAsync", "AddRange", "AddRangeAsync", "Update", "UpdateRange",
        "Remove", "RemoveRange", "ExecuteDelete", "ExecuteDeleteAsync", "ExecuteUpdate", "ExecuteUpdateAsync",
    };

    private static readonly Regex ExecProc = new(@"\bEXEC(?:UTE)?\s+(?<name>[\[\]\w]+(?:\.[\[\]\w]+){0,2})",
        RegexOptions.IgnoreCase);
    private static readonly Regex TableRef = new(@"\b(?:FROM|JOIN|INTO|UPDATE)\s+(?<name>[\[\]\w]+(?:\.[\[\]\w]+){0,2})",
        RegexOptions.IgnoreCase);

    // Lazy<T> (default mode: ExecutionAndPublication) makes this safe under xUnit's
    // parallel test classes, which all funnel through Extract() concurrently: without
    // it, a plain "if (_runtimeRefs is null)" cache publishes the list to the static
    // field before it is filled, so a racing thread can read it half-populated and
    // build a Roslyn compilation with missing runtime references — silently wrong,
    // nondeterministic symbol resolution rather than a crash.
    private static readonly Lazy<List<MetadataReference>> _runtimeRefs = new(BuildRuntimeRefs);

    /// <summary>
    /// Optional nodestore model.json path with the SQL catalog. Without it the
    /// extractor still emits app structure, but no bridge edges.
    /// </summary>
    public string? CatalogPath { get; set; }

    /// <summary>Also emit AMBIGUOUS candidate edges (catalog matches of an unresolved template).</summary>
    public bool IncludeAmbiguous { get; set; } = true;

    /// <summary>
    /// Extract anyway when the input contains projects outside scope. Off by default:
    /// a partial graph presented as complete produces confidently wrong impact answers.
    /// When on, the excluded projects are written as nodes with analyzed=false so the
    /// gap is visible to whoever reads the graph, not only to whoever ran the command.
    /// </summary>
    public bool AllowPartial { get; set; }

    public string Name => "net";

    public bool CanHandle(string inputPath) =>
        inputPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
        || inputPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
        || (Directory.Exists(inputPath) &&
            (Directory.EnumerateFiles(inputPath, "*.sln", SearchOption.AllDirectories).Any() ||
             Directory.EnumerateFiles(inputPath, "*.csproj", SearchOption.AllDirectories).Any()));

    // Two Extract() calls building their compilation graphs on different threads at
    // the same time is not safe: each project's compilation references another via
    // CSharpCompilation.ToMetadataReference() (a project referencing a sibling one),
    // and Roslyn's cross-compilation symbol resolution over that kind of reference is
    // not reentrant under true concurrent use — confirmed by a stress harness running
    // two fully independent Extract() calls (distinct directories, distinct symbols)
    // concurrently: a CALLS edge into the referenced project's method is intermittently
    // (~1-3%) missing, purely from the concurrency, never when run sequentially however
    // many times. Serializing only this critical section (not all of Extract, and not
    // xUnit's parallelism generally) keeps that concurrency safe for any caller.
    private static readonly object _buildLock = new();

    public GraphPayload Extract(string inputPath)
    {
        var sln = SolutionLoader.Load(inputPath);
        if (sln.Unsupported.Count > 0 && !AllowPartial)
            throw new UnsupportedProjectException(sln.Unsupported);

        var catalog = CatalogPath is not null ? Catalog.Load(CatalogPath) : null;
        var b = new Builder(sln, catalog, IncludeAmbiguous);
        lock (_buildLock)
            return b.Build();
    }

    private static IReadOnlyList<MetadataReference> RuntimeRefs() => _runtimeRefs.Value;

    private static List<MetadataReference> BuildRuntimeRefs()
    {
        var refs = new List<MetadataReference>();
        var tpa = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "";
        foreach (var path in tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                refs.Add(MetadataReference.CreateFromFile(path));
        return refs;
    }

    // ------------------------------------------------------------------
    private sealed class Builder
    {
        private readonly SolutionInfo _sln;
        private readonly Catalog? _catalog;
        private readonly bool _includeAmbiguous;
        // Two Extract() calls running on different threads (e.g. two test classes
        // extracting the same fixture concurrently) must never hand Roslyn two
        // CSharpCompilation objects with the same assembly name: cross-compilation
        // symbol resolution (a project referencing another via ToMetadataReference)
        // becomes nondeterministic when that name collides across compilations built
        // on separate threads at the same time, intermittently failing to resolve
        // calls into the referenced project. proj.Name is still the key used
        // everywhere else (the dictionary below, ids, etc.) — only the identity
        // Roslyn sees is disambiguated.
        private readonly string _asmSuffix = Guid.NewGuid().ToString("N");

        private readonly Dictionary<string, GraphNode> _nodes = new(StringComparer.Ordinal);
        private readonly List<GraphRel> _edges = new();
        private readonly HashSet<string> _edgeKeys = new(StringComparer.Ordinal);

        private readonly Dictionary<IMethodSymbol, string> _methodIds = new(SymbolEqualityComparer.Default);
        private readonly Dictionary<INamedTypeSymbol, string> _classIds = new(SymbolEqualityComparer.Default);
        // (declared method symbol, parameter ordinal) -> literal argument values seen at call sites
        private readonly Dictionary<(IMethodSymbol Method, int Ordinal), HashSet<string>> _callSiteLiterals =
            new(new CallSiteKeyComparer());
        // sinks whose template still has a parameter hole, waiting for call-site narrowing
        private readonly List<PendingTemplate> _pending = new();
        // interface member -> concrete implementations declared in the solution
        private readonly Dictionary<IMethodSymbol, List<(INamedTypeSymbol Type, IMethodSymbol Member)>> _ifaceImpls =
            new(SymbolEqualityComparer.Default);
        // interface type -> implementations bound in DI registrations
        private readonly Dictionary<INamedTypeSymbol, List<INamedTypeSymbol>> _diBindings =
            new(SymbolEqualityComparer.Default);
        // interface type -> implementations instantiated with `new` (composition without a container)
        private readonly Dictionary<INamedTypeSymbol, List<INamedTypeSymbol>> _newBindings =
            new(SymbolEqualityComparer.Default);
        // calls landing on an interface member, waiting for the implementation to be picked
        private readonly List<PendingInterfaceCall> _pendingIfaceCalls = new();
        // public methods of class libraries: entry points only if nothing in the solution calls them
        private readonly List<LibraryApiCandidate> _libraryApiCandidates = new();
        // architecture indexes: who owns what, so class-level coupling can be rolled up
        private readonly Dictionary<string, string> _methodOwner = new(StringComparer.Ordinal);   // methodId -> classId
        private readonly Dictionary<string, string> _classNamespace = new(StringComparer.Ordinal); // classId -> namespace
        private readonly Dictionary<string, string> _classProject = new(StringComparer.Ordinal);   // classId -> project name
        // DbSet property name -> entity type name (per solution; names are enough at this scale)
        private readonly Dictionary<string, string> _dbSetEntity = new(StringComparer.Ordinal);
        // entity type name -> resolved table id
        private readonly Dictionary<string, string> _entityTable = new(StringComparer.Ordinal);

        private sealed record PendingTemplate(
            string MethodId, SqlStringTemplate Template, bool IsProcName, int Line, string File, string? Conditions);

        private sealed record PendingInterfaceCall(
            string CallerId, IMethodSymbol Member, int Line, string File);

        private sealed record LibraryApiCandidate(
            string MethodId, string ClassId, string Key, int Line, string File);

        public Builder(SolutionInfo sln, Catalog? catalog, bool includeAmbiguous)
        {
            _sln = sln;
            _catalog = catalog;
            _includeAmbiguous = includeAmbiguous;
        }

        public GraphPayload Build()
        {
            string slnId = $"app::{_sln.Name}";
            AddNode(slnId, "AppSolution", new() { ["name"] = _sln.Name, ["path"] = _sln.RootDir });

            // Excluded projects are part of the graph, flagged: a consumer walking the
            // solution sees the hole instead of concluding it does not exist.
            foreach (var skipped in _sln.Unsupported)
            {
                string skippedId = $"app::proj:{skipped.Name}";
                AddNode(skippedId, "AppProject", new()
                {
                    ["name"] = skipped.Name,
                    ["kind"] = "unsupported",
                    ["path"] = skipped.Path,
                    ["analyzed"] = false,
                    ["unsupported_reason"] = skipped.Reason,
                });
                AddEdge("CONTAINS", slnId, skippedId);
            }

            var compilations = new Dictionary<string, CSharpCompilation>(StringComparer.OrdinalIgnoreCase);
            var trees = new Dictionary<SyntaxTree, (ProjectInfo Proj, string FileId)>();

            foreach (var proj in SolutionLoader.TopoSort(_sln))
            {
                string projId = $"app::proj:{proj.Name}";
                var projProps = new Dictionary<string, object>
                {
                    ["name"] = proj.Name, ["kind"] = proj.Kind, ["path"] = Rel(proj.CsprojPath),
                };
                if (proj.ConnectionStrings.Count > 0)
                {
                    projProps["connection_strings"] = proj.ConnectionStrings.Select(c => c.Name).ToList();
                    var dbs = proj.ConnectionStrings.Where(c => c.Database is not null)
                        .Select(c => c.Database!).Distinct().ToList();
                    if (dbs.Count > 0) projProps["databases"] = dbs;
                }
                AddNode(projId, "AppProject", projProps);
                AddEdge("CONTAINS", slnId, projId);

                foreach (var (pkg, version) in proj.PackageReferences)
                {
                    string pkgId = $"app::pkg:{pkg}";
                    AddNode(pkgId, "AppPackage", new() { ["name"] = pkg, ["version"] = version });
                    AddEdge("DEPENDS_ON", projId, pkgId, new() { ["kind"] = "package", ["version"] = version });
                }
                foreach (var dep in proj.ProjectReferences)
                    AddEdge("DEPENDS_ON", projId, $"app::proj:{dep}", new() { ["kind"] = "project" });
                foreach (var asm in proj.AssemblyReferences)
                {
                    string asmId = $"app::asm:{asm}";
                    AddNode(asmId, "AppPackage", new() { ["name"] = asm, ["kind"] = "assembly" });
                    AddEdge("DEPENDS_ON", projId, asmId, new() { ["kind"] = "assembly" });
                }

                var projTrees = new List<SyntaxTree>();
                foreach (var file in proj.SourceFiles)
                {
                    var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file);
                    projTrees.Add(tree);
                    string fileId = $"app::file:{Rel(file)}";
                    AddNode(fileId, "AppFile", new() { ["name"] = Path.GetFileName(file), ["path"] = Rel(file) });
                    AddEdge("CONTAINS", projId, fileId);
                    trees[tree] = (proj, fileId);
                }

                var refs = new List<MetadataReference>(RuntimeRefs());
                foreach (var dep in proj.ProjectReferences)
                    if (compilations.TryGetValue(dep, out var depComp))
                        refs.Add(depComp.ToMetadataReference());

                compilations[proj.Name] = CSharpCompilation.Create($"{proj.Name}~{_asmSuffix}", projTrees, refs,
                    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            }

            // Pass 1: declarations (classes, methods, CONTAINS) + EF model mapping.
            foreach (var comp in compilations.Values)
                foreach (var tree in comp.SyntaxTrees)
                    DeclarationPass(comp.GetSemanticModel(tree), tree, trees[tree].FileId, trees[tree].Proj);

            // Pass 2: calls + call-site literals, SQL sinks, EF usage, rules.
            foreach (var comp in compilations.Values)
                foreach (var tree in comp.SyntaxTrees)
                    BodyPass(comp.GetSemanticModel(tree), tree, trees[tree].Proj.Name);

            // Pass 3: narrow pending templates from call-site literals, and walk
            // interface calls through to the implementation bound by DI.
            foreach (var pending in _pending)
                EmitTemplate(pending);
            ResolveInterfaceCalls();
            EmitLibraryApiEntryPoints();
            EmitArchitectureDependencies();

            int i = 0;
            foreach (var e in _edges) e.Id = $"a{i++}";
            var payload = new GraphPayload();
            payload.Nodes.AddRange(_nodes.Values);
            payload.Relationships.AddRange(_edges);
            return payload;
        }

        // ---------------- pass 1: declarations + EF model ----------------
        private void DeclarationPass(SemanticModel model, SyntaxTree tree, string fileId, ProjectInfo project)
        {
            string projectKind = project.Kind;
            var root = tree.GetRoot();
            foreach (var typeDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(typeDecl) is not INamedTypeSymbol typeSymbol) continue;

                string classId = $"app::{Fqn(typeSymbol)}";
                _classIds[typeSymbol] = classId;
                AddNode(classId, "AppClass", new()
                {
                    ["name"] = typeSymbol.Name,
                    ["namespace"] = typeSymbol.ContainingNamespace?.ToDisplayString() ?? "",
                    ["kind"] = typeSymbol.IsRecord ? "record" : typeSymbol.TypeKind switch
                    {
                        TypeKind.Interface => "interface",
                        TypeKind.Struct => "struct",
                        TypeKind.Enum => "enum",
                        _ => "class",
                    },
                    ["generic_arity"] = typeSymbol.Arity,
                    ["path"] = Rel(tree.FilePath),
                    ["line"] = Line(typeDecl),
                });
                AddEdge("CONTAINS", fileId, classId);

                // Namespace as a node, not just a property: it is the unit a layer map
                // is drawn in, and the only one that survives moving files around.
                string ns = typeSymbol.ContainingNamespace?.ToDisplayString() ?? "";
                _classProject[classId] = project.Name;
                if (!string.IsNullOrEmpty(ns) && ns != "<global namespace>")
                {
                    _classNamespace[classId] = ns;
                    string nsId = $"app::ns:{ns}";
                    AddNode(nsId, "AppNamespace", new() { ["name"] = ns });
                    AddEdge("BELONGS_TO", classId, nsId);
                }

                // Interfaces implemented / base class, when declared in this solution.
                foreach (var iface in typeSymbol.Interfaces)
                    if (iface.Locations.Any(l => l.IsInSource))
                        AddEdge("IMPLEMENTS", classId, $"app::{Fqn(iface)}");
                if (typeSymbol.BaseType is { SpecialType: SpecialType.None } baseType &&
                    baseType.Locations.Any(l => l.IsInSource))
                    AddEdge("DEPENDS_ON", classId, $"app::{Fqn(baseType)}", new() { ["kind"] = "inherits" });

                // DI: constructor-injected dependencies (the modular-app coupling map).
                foreach (var ctor in typeDecl.Members.OfType<ConstructorDeclarationSyntax>())
                    foreach (var param in ctor.ParameterList.Parameters)
                        if (param.Type is not null &&
                            model.GetSymbolInfo(param.Type).Symbol is INamedTypeSymbol depType &&
                            depType.Locations.Any(l => l.IsInSource))
                            AddEdge("DEPENDS_ON", classId, $"app::{Fqn(depType)}", new() { ["kind"] = "injected" });

                bool isController = typeSymbol.Name.EndsWith("Controller", StringComparison.Ordinal)
                    || typeDecl.AttributeLists.SelectMany(l => l.Attributes)
                        .Any(a => a.Name.ToString() is "ApiController" or "ApiControllerAttribute");
                string? controllerRoute = isController ? RouteFromAttributes(typeDecl.AttributeLists, typeSymbol.Name) : null;

                var methodDecls = typeDecl.Members.OfType<MethodDeclarationSyntax>().ToList();
                foreach (var methodDecl in methodDecls)
                {
                    if (model.GetDeclaredSymbol(methodDecl) is not IMethodSymbol ms) continue;
                    bool overloaded = methodDecls.Count(m => m.Identifier.ValueText == methodDecl.Identifier.ValueText) > 1;
                    string methodId = $"app::{Fqn(typeSymbol)}.{ms.Name}" + (overloaded ? $"({ms.Parameters.Length})" : "");
                    _methodIds[ms] = methodId;
                    _methodOwner[methodId] = classId;
                    AddNode(methodId, "AppMethod", new()
                    {
                        ["name"] = ms.Name,
                        ["class"] = typeSymbol.Name,
                        ["path"] = Rel(tree.FilePath),
                        ["line"] = Line(methodDecl),
                    });
                    AddEdge("CONTAINS", classId, methodId);

                    if (isController)
                        EmitControllerEndpoint(methodDecl, classId, methodId, controllerRoute);

                    CollectNonHttpEntryPoints(typeDecl, typeSymbol, ms, classId, methodId, projectKind,
                        Line(methodDecl), Rel(tree.FilePath));

                    // A class library has no entry point of its own: its callers live
                    // outside the solution. Its public surface is the way in, decided
                    // in pass 3 once every intra-solution call is known.
                    if (projectKind == "library" &&
                        ms.DeclaredAccessibility == Accessibility.Public &&
                        typeSymbol.DeclaredAccessibility == Accessibility.Public &&
                        typeSymbol.TypeKind != TypeKind.Interface)
                        _libraryApiCandidates.Add(new LibraryApiCandidate(
                            methodId, classId, $"{typeSymbol.Name}.{ms.Name}", Line(methodDecl), Rel(tree.FilePath)));
                }

                CollectInterfaceImplementations(typeSymbol);
                CollectEfModel(typeDecl, typeSymbol, model);
            }
        }

        /// <summary>
        /// Indexes, for every interface member declared in this solution, the concrete
        /// members that implement it. This is what lets a call landing on the interface
        /// be walked through to the code that actually runs (see ResolveInterfaceCalls).
        /// Abstract/virtual dispatch through base classes is not covered in v1.
        /// </summary>
        private void CollectInterfaceImplementations(INamedTypeSymbol typeSymbol)
        {
            if (typeSymbol.TypeKind is not (TypeKind.Class or TypeKind.Struct) || typeSymbol.IsAbstract) return;

            foreach (var iface in typeSymbol.AllInterfaces)
            {
                if (!iface.Locations.Any(l => l.IsInSource)) continue;

                foreach (var member in iface.GetMembers().OfType<IMethodSymbol>())
                {
                    if (typeSymbol.FindImplementationForInterfaceMember(member) is not IMethodSymbol impl) continue;

                    var key = (IMethodSymbol)member.OriginalDefinition;
                    if (!_ifaceImpls.TryGetValue(key, out var list))
                        _ifaceImpls[key] = list = new List<(INamedTypeSymbol, IMethodSymbol)>();
                    list.Add((typeSymbol, (IMethodSymbol)impl.OriginalDefinition));
                }
            }
        }

        private void CollectEfModel(TypeDeclarationSyntax typeDecl, INamedTypeSymbol typeSymbol, SemanticModel model)
        {
            // Entity -> table via [Table("N", Schema = "S")]
            foreach (var attr in typeDecl.AttributeLists.SelectMany(l => l.Attributes))
            {
                var attrName = attr.Name.ToString();
                if (attrName is "Table" or "TableAttribute" && attr.ArgumentList?.Arguments.Count > 0)
                {
                    string? table = ConstString(attr.ArgumentList.Arguments[0].Expression);
                    string? schema = attr.ArgumentList.Arguments
                        .FirstOrDefault(a => a.NameEquals?.Name.Identifier.ValueText == "Schema")
                        ?.Expression is { } se ? ConstString(se) : null;
                    MapEntity(typeSymbol.Name, table, schema, "attribute");
                }
            }

            bool isContext = InheritsDbContext(typeDecl);
            if (!isContext) return;

            // DbSet<T> properties: convention mapping + usage index.
            foreach (var prop in typeDecl.Members.OfType<PropertyDeclarationSyntax>())
            {
                if (prop.Type is GenericNameSyntax { Identifier.ValueText: "DbSet" } g &&
                    g.TypeArgumentList.Arguments.Count == 1)
                {
                    string entity = g.TypeArgumentList.Arguments[0].ToString().Split('.').Last();
                    _dbSetEntity[prop.Identifier.ValueText] = entity;
                    if (!_entityTable.ContainsKey(entity))
                        MapEntity(entity, prop.Identifier.ValueText, schema: null, "convention");
                }
            }

            // Fluent mapping: modelBuilder.Entity<T>()....ToTable("N"[, "S"])
            foreach (var inv in typeDecl.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (inv.Expression is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: "ToTable" }) continue;
                var entityGeneric = inv.DescendantNodes().OfType<GenericNameSyntax>()
                    .FirstOrDefault(gn => gn.Identifier.ValueText == "Entity" && gn.TypeArgumentList.Arguments.Count == 1);
                if (entityGeneric is null) continue;

                var args = inv.ArgumentList.Arguments;
                string? table = args.Count > 0 ? ConstString(args[0].Expression) : null;
                string? schema = args.Count > 1 ? ConstString(args[1].Expression) : null;
                MapEntity(entityGeneric.TypeArgumentList.Arguments[0].ToString().Split('.').Last(),
                    table, schema, "fluent");
            }
        }

        private void MapEntity(string entityName, string? table, string? schema, string via)
        {
            if (_catalog is null || table is null) return;

            string id = "";
            bool ok = schema is not null
                ? _catalog.TryResolveTable($"{schema}.{table}", out id)
                : _catalog.TryResolveTable(table, out id) || _catalog.TryResolveTableBareName(table, out id);
            if (!ok) return;

            _entityTable[entityName] = id;
            // MAPS_TO is emitted when the entity class node exists (it may be
            // declared later in pass 1); resolve lazily at the end of pass 2 via
            // _entityTable + _classIds instead of ordering constraints.
        }

        // ---------------- endpoints (Web API) ----------------
        private static readonly Dictionary<string, string> HttpAttrVerbs = new(StringComparer.Ordinal)
        {
            ["HttpGet"] = "GET", ["HttpPost"] = "POST", ["HttpPut"] = "PUT",
            ["HttpDelete"] = "DELETE", ["HttpPatch"] = "PATCH", ["HttpHead"] = "HEAD", ["HttpOptions"] = "OPTIONS",
        };

        private static readonly Dictionary<string, string> MinimalApiVerbs = new(StringComparer.Ordinal)
        {
            ["MapGet"] = "GET", ["MapPost"] = "POST", ["MapPut"] = "PUT",
            ["MapDelete"] = "DELETE", ["MapPatch"] = "PATCH",
        };

        private void EmitControllerEndpoint(MethodDeclarationSyntax methodDecl,
            string classId, string methodId, string? controllerRoute)
        {
            foreach (var attr in methodDecl.AttributeLists.SelectMany(l => l.Attributes))
            {
                string attrName = attr.Name.ToString().Replace("Attribute", "");
                if (!HttpAttrVerbs.TryGetValue(attrName, out var verb)) continue;

                string? methodRoute = attr.ArgumentList?.Arguments.Count > 0
                    ? ConstString(attr.ArgumentList.Arguments[0].Expression) : null;
                string route = CombineRoute(controllerRoute, methodRoute);
                AddEndpoint(verb, route, classId, methodId, Line(methodDecl), Rel(methodDecl.SyntaxTree.FilePath));
            }
        }

        private void EmitMinimalApiEndpoint(InvocationExpressionSyntax inv, SemanticModel model, string enclosingMethodId)
        {
            if (inv.Expression is not MemberAccessExpressionSyntax member ||
                !MinimalApiVerbs.TryGetValue(member.Name.Identifier.ValueText, out var verb))
                return;
            var args = inv.ArgumentList.Arguments;
            if (args.Count < 2 || ConstString(args[0].Expression) is not { } route) return;

            // Handler: a method group resolvable in-solution, else the registering method.
            string handlerId = enclosingMethodId;
            if (model.GetSymbolInfo(args[1].Expression).Symbol is IMethodSymbol handler &&
                _methodIds.TryGetValue((IMethodSymbol)handler.OriginalDefinition, out var resolved))
                handlerId = resolved;

            AddEndpoint(verb, route, ownerId: null, handlerId, Line(inv), Rel(inv.SyntaxTree.FilePath));
        }

        private void AddEndpoint(string verb, string route, string? ownerId, string handlerId, int line, string file)
        {
            var props = new Dictionary<string, object> { ["verb"] = verb, ["route"] = route };
            AddEntryPoint("http_route", $"{verb} {route}", ownerId, handlerId, line, file, props);
        }

        /// <summary>
        /// Registers where a flow starts. An HTTP route is one kind among several: a
        /// console Main, a hosted service, a UI handler or a timer job start flows the
        /// same way and reach the same database. Callers pass the kind; the shape of
        /// the node and the edges to the handler never change.
        /// </summary>
        private void AddEntryPoint(string kind, string key, string? ownerId, string handlerId,
            int line, string file, Dictionary<string, object>? extra = null)
        {
            string entryId = $"app::entry:{kind}:{key}";
            var props = new Dictionary<string, object>
            {
                ["name"] = key, ["kind"] = kind, ["path"] = file, ["line"] = line,
            };
            if (extra is not null)
                foreach (var (k, v) in extra) props[k] = v;

            AddNode(entryId, "EntryPoint", props);
            if (ownerId is not null) AddEdge("EXPOSES", ownerId, entryId);
            AddEdge("CALLS", entryId, handlerId);
        }

        /// <summary>
        /// Entry points that no routing table declares: the Main of a console/batch job,
        /// and the method a hosted or Windows service runs. These are the way in for
        /// every project that is not a web API — services, batch and scheduled work.
        /// </summary>
        private void CollectNonHttpEntryPoints(TypeDeclarationSyntax typeDecl, INamedTypeSymbol typeSymbol,
            IMethodSymbol ms, string classId, string methodId, string projectKind, int line, string file)
        {
            if (ms.IsStatic && ms.Name == "Main" && projectKind is not ("library" or "test"))
            {
                AddEntryPoint("console_main", $"{typeSymbol.Name}.Main", classId, methodId, line, file);
                return;
            }

            // BackgroundService.ExecuteAsync / IHostedService.StartAsync / legacy
            // ServiceBase.OnStart: the loop a service actually runs. Matched on names
            // as well as symbols — without a restore these base types do not resolve.
            if (ms.Name is not ("ExecuteAsync" or "StartAsync" or "OnStart" or "Execute")) return;

            var baseNames = BaseTypeNames(typeSymbol)
                .Concat(typeSymbol.AllInterfaces.Select(i => i.Name))
                .Concat(typeDecl.BaseList?.Types.Select(t => t.Type.ToString().Split('.').Last().Split('<')[0])
                        ?? Enumerable.Empty<string>())
                .ToList();

            // A scheduler's job (Quartz IJob and friends) is a process too: nothing
            // calls it from the code, it is started from outside on a clock.
            if (baseNames.Any(n => n is "IJob" or "IJobExecutor" or "IInvocable") && ms.Name is "Execute" or "ExecuteAsync")
            {
                AddEntryPoint("job", $"{typeSymbol.Name}.{ms.Name}", classId, methodId, line, file);
                return;
            }

            if (baseNames.Any(IsHostedBase) && ms.Name is not "Execute")
                AddEntryPoint("hosted_service", $"{typeSymbol.Name}.{ms.Name}", classId, methodId, line, file);
        }

        private static bool IsHostedBase(string name) =>
            name is "BackgroundService" or "ServiceBase" or "IHostedService";

        private static IEnumerable<string> BaseTypeNames(INamedTypeSymbol type)
        {
            for (var t = type.BaseType; t is not null; t = t.BaseType)
                yield return t.Name;
        }

        private static string? RouteFromAttributes(SyntaxList<AttributeListSyntax> lists, string typeName)
        {
            foreach (var attr in lists.SelectMany(l => l.Attributes))
                if (attr.Name.ToString() is "Route" or "RouteAttribute" && attr.ArgumentList?.Arguments.Count > 0
                    && ConstString(attr.ArgumentList.Arguments[0].Expression) is { } template)
                    return template.Replace("[controller]",
                        typeName.EndsWith("Controller", StringComparison.Ordinal) ? typeName[..^"Controller".Length].ToLowerInvariant() : typeName.ToLowerInvariant());
            return null;
        }

        private static string CombineRoute(string? controllerRoute, string? methodRoute)
        {
            string a = (controllerRoute ?? "").Trim('/');
            string b = (methodRoute ?? "").Trim('/');
            string combined = b.Length == 0 ? a : (a.Length == 0 ? b : $"{a}/{b}");
            return "/" + combined;
        }

        // ---------------- pass 2: bodies ----------------
        private void BodyPass(SemanticModel model, SyntaxTree tree, string projectName)
        {
            var root = tree.GetRoot();
            CollectNewBindings(model, root);

            foreach (var methodDecl in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(methodDecl) is not IMethodSymbol ms || !_methodIds.TryGetValue(ms, out var methodId))
                    continue;
                ScanExecutableScope(methodDecl, model, methodId);
            }

            // Top-level statements (Program.cs main run): a synthetic Main method
            // so startup wiring (DI, minimal APIs, sinks) has an owner node.
            if (root is CompilationUnitSyntax unit)
            {
                var topLevel = unit.Members.OfType<GlobalStatementSyntax>().ToList();
                if (topLevel.Count > 0)
                {
                    string mainId = $"app::{projectName}.Program.Main";
                    AddNode(mainId, "AppMethod", new()
                    {
                        ["name"] = "Main", ["class"] = "Program", ["kind"] = "top-level",
                        ["path"] = Rel(tree.FilePath), ["line"] = 1,
                    });
                    foreach (var statement in topLevel)
                        ScanExecutableScope(statement, model, mainId);
                }
            }

            // MAPS_TO once both sides are known.
            foreach (var (typeSymbol, classId) in _classIds)
                if (_entityTable.TryGetValue(typeSymbol.Name, out var tableId))
                    AddEdge("MAPS_TO", classId, tableId);
        }

        private void ScanExecutableScope(SyntaxNode scope, SemanticModel model, string methodId)
        {
            bool usesStoredProcType = scope.DescendantNodes()
                .OfType<MemberAccessExpressionSyntax>()
                .Any(m => m.Name.Identifier.ValueText == "StoredProcedure");

            foreach (var node in scope.DescendantNodes())
            {
                switch (node)
                {
                    case InvocationExpressionSyntax inv:
                        HandleInvocation(inv, model, methodId);
                        break;
                    case ObjectCreationExpressionSyntax oc when TypeNameEndsWith(oc.Type, "SqlCommand"):
                        if (oc.ArgumentList?.Arguments.Count > 0)
                            HandleSqlText(oc.ArgumentList.Arguments[0].Expression, model, methodId,
                                isProcName: usesStoredProcType, oc);
                        break;
                    case AssignmentExpressionSyntax
                    {
                        Left: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "CommandText" }
                    } assign:
                        HandleSqlText(assign.Right, model, methodId, isProcName: usesStoredProcType, assign);
                        break;
                }
            }

            EmitEfUsage(scope, methodId);
        }

        private void HandleInvocation(InvocationExpressionSyntax inv, SemanticModel model, string callerId)
        {
            EmitMinimalApiEndpoint(inv, model, callerId);

            // CALLS + call-site literals (intra-solution).
            if (model.GetSymbolInfo(inv).Symbol is IMethodSymbol target)
            {
                var original = (IMethodSymbol)target.OriginalDefinition;
                if (_methodIds.TryGetValue(original, out var calleeId))
                {
                    AddEdge("CALLS", callerId, calleeId);
                    var args = inv.ArgumentList.Arguments;
                    for (int i = 0; i < args.Count && i < original.Parameters.Length; i++)
                        if (args[i].NameColon is null &&
                            args[i].Expression is LiteralExpressionSyntax lit &&
                            lit.IsKind(SyntaxKind.StringLiteralExpression))
                            Record(_callSiteLiterals, (original, i), lit.Token.ValueText);

                    // The edge above stops at the interface; the implementation is
                    // picked once every DI registration has been seen (pass 3).
                    if (original.ContainingType?.TypeKind == TypeKind.Interface)
                        _pendingIfaceCalls.Add(new PendingInterfaceCall(
                            callerId, original, Line(inv), Rel(inv.SyntaxTree.FilePath)));
                }
            }

            // DI registrations: services.AddScoped<IFoo, Foo>() and friends —
            // where the interface->implementation binding is decided.
            if (inv.Expression is MemberAccessExpressionSyntax { Name: GenericNameSyntax gen } diMember &&
                gen.Identifier.ValueText is "AddScoped" or "AddTransient" or "AddSingleton" &&
                gen.TypeArgumentList.Arguments.Count == 2)
            {
                var iface = model.GetSymbolInfo(gen.TypeArgumentList.Arguments[0]).Symbol as INamedTypeSymbol;
                var impl = model.GetSymbolInfo(gen.TypeArgumentList.Arguments[1]).Symbol as INamedTypeSymbol;
                if (iface is not null && impl is not null &&
                    iface.Locations.Any(l => l.IsInSource) && impl.Locations.Any(l => l.IsInSource))
                {
                    AddEdge("DEPENDS_ON", $"app::{Fqn(iface)}", $"app::{Fqn(impl)}", new()
                    {
                        ["kind"] = "di",
                        ["lifetime"] = gen.Identifier.ValueText["Add".Length..].ToLowerInvariant(),
                        ["registered_in"] = callerId,
                        ["line"] = Line(inv),
                    });
                    RecordDiBinding(iface, impl);
                }
            }
            // Non-generic form: services.AddScoped(typeof(IFoo), typeof(Foo)).
            else if (inv.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: var addName } &&
                     addName is "AddScoped" or "AddTransient" or "AddSingleton" &&
                     inv.ArgumentList.Arguments.Count == 2 &&
                     inv.ArgumentList.Arguments[0].Expression is TypeOfExpressionSyntax ifaceOf &&
                     inv.ArgumentList.Arguments[1].Expression is TypeOfExpressionSyntax implOf)
            {
                var iface = model.GetSymbolInfo(ifaceOf.Type).Symbol as INamedTypeSymbol;
                var impl = model.GetSymbolInfo(implOf.Type).Symbol as INamedTypeSymbol;
                if (iface is not null && impl is not null &&
                    iface.Locations.Any(l => l.IsInSource) && impl.Locations.Any(l => l.IsInSource))
                {
                    AddEdge("DEPENDS_ON", $"app::{Fqn(iface)}", $"app::{Fqn(impl)}", new()
                    {
                        ["kind"] = "di",
                        ["lifetime"] = addName["Add".Length..].ToLowerInvariant(),
                        ["registered_in"] = callerId,
                        ["line"] = Line(inv),
                    });
                    RecordDiBinding(iface, impl);
                }
            }

            // Dapper / EF raw sinks (syntax fallback: no restore of the target app).
            if (inv.Expression is MemberAccessExpressionSyntax member)
            {
                string name = member.Name.Identifier.ValueText;

                // Outbound HTTP calls to other services.
                if (HttpClientMethods.Contains(name) &&
                    inv.ArgumentList.Arguments.Count > 0)
                {
                    var url = StringResolver.Resolve(inv.ArgumentList.Arguments[0].Expression, model);
                    bool known = url.HasKnownText;
                    string urlText = known ? url.Literal ?? url.ToString() : "";
                    string key = known && Uri.TryCreate(urlText, UriKind.Absolute, out var abs)
                        ? abs.Host
                        : known ? urlText : Boundary.UnknownKey;

                    // An HTTP sink whose target cannot be read is still infrastructure
                    // this method touches: reported as UNRESOLVED rather than dropped.
                    string targetId = Boundary.TargetId("http", key);
                    AddNode(targetId, Boundary.TargetLabel, new()
                    {
                        ["name"] = key, ["protocol"] = "http",
                    });
                    var props = Boundary.Props("http",
                        known ? (url.Literal is not null ? "EXTRACTED" : "RESOLVED") : "UNRESOLVED",
                        known ? (url.Literal is not null ? "literal" : "local_flow") : "unresolved",
                        known ? urlText : name,
                        Line(inv), Rel(inv.SyntaxTree.FilePath));
                    if (known) props["url"] = urlText;
                    AddEdge(Boundary.ExternalEdge, callerId, targetId, props,
                        dedupeKeyExtra: Line(inv).ToString());
                }

                bool dapper = DapperMethods.Contains(name);
                bool efRaw = EfRawMethods.Contains(name);
                if (!dapper && !efRaw) return;

                var sqlArg = inv.ArgumentList.Arguments
                    .FirstOrDefault(a => a.NameColon is null)?.Expression;
                if (sqlArg is null) return;

                bool storedProc = inv.ArgumentList.Arguments.Any(a =>
                    a.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "StoredProcedure" });

                HandleSqlText(sqlArg, model, callerId, isProcName: storedProc, inv);
            }
        }

        private void HandleSqlText(ExpressionSyntax expr, SemanticModel model, string methodId, bool isProcName, SyntaxNode site)
        {
            if (_catalog is null) return;

            var template = StringResolver.Resolve(expr, model);
            string? conditions = GoverningConditions(site, methodId);
            // Line of the SQL argument, not the sink call: multi-line invocations
            // report where the SQL text actually lives (matches the spike oracle).
            int line = Line(expr);
            string file = Rel(site.SyntaxTree.FilePath);

            if (template.Literal is { } literal)
            {
                EmitFromSqlText(literal, methodId, isProcName, line, file, "EXTRACTED", conditions, literal);
                return;
            }

            if (!template.HasKnownText) return;

            if (isProcName || LooksLikeBareName(template))
            {
                _pending.Add(new PendingTemplate(methodId, template, true, line, file, conditions));
            }
            else
            {
                // SQL text with holes (params in WHERE etc.): parse the known parts.
                EmitFromSqlText(template.Substitute(" ¤ "), methodId, false, line, file, "EXTRACTED",
                    conditions, template.ToString());
            }
        }

        private static bool LooksLikeBareName(SqlStringTemplate t) =>
            t.Parts.OfType<string>().All(s => !s.Contains(' ')) &&
            !t.Parts.OfType<string>().Any(s => s.Contains("SELECT", StringComparison.OrdinalIgnoreCase));

        private void EmitFromSqlText(string sql, string methodId, bool isProcName, int line, string file,
            string confidence, string? conditions, string matched)
        {
            string trimmed = sql.Trim();

            if (isProcName && _catalog!.TryResolveProc(trimmed, out var procId))
            {
                AddBridge(methodId, procId, "proc", confidence, line, file, matched, conditions);
                return;
            }

            bool any = false;
            foreach (Match m in ExecProc.Matches(sql))
                if (_catalog!.TryResolveProc(m.Groups["name"].Value, out var id))
                {
                    AddBridge(methodId, id, "proc", confidence, line, file, matched, conditions);
                    any = true;
                }

            foreach (Match m in TableRef.Matches(sql))
                if (_catalog!.TryResolveTable(m.Groups["name"].Value, out var id))
                {
                    AddBridge(methodId, id, "table", confidence, line, file, matched, conditions);
                    any = true;
                }

            // Whole text is a proc name (CommandText = "Schema.Proc" without an
            // explicit CommandType in sight).
            if (!any && !isProcName && _catalog!.TryResolveProc(trimmed, out var lastId))
                AddBridge(methodId, lastId, "proc", confidence, line, file, matched, conditions);
        }

        private void EmitTemplate(PendingTemplate p)
        {
            // 1-level interprocedural narrowing: the hole is a parameter and some
            // call site passes a literal for it.
            if (p.Template.SingleParameterHole is { Parameter: { } param })
            {
                var owner = (IMethodSymbol)param.ContainingSymbol.OriginalDefinition;
                if (_callSiteLiterals.TryGetValue((owner, param.Ordinal), out var values))
                {
                    foreach (var v in values)
                    {
                        string candidate = p.Template.Substitute(v);
                        if (_catalog!.TryResolveProc(candidate, out var id))
                            AddBridge(p.MethodId, id, "proc", "RESOLVED", p.Line, p.File, candidate, p.Conditions);
                    }
                    return;
                }
            }

            if (!_includeAmbiguous) return;

            var regex = p.Template.ToRegex();
            foreach (var (name, id) in _catalog!.Procs)
                if (regex.IsMatch(name))
                    AddBridge(p.MethodId, id, "proc", "AMBIGUOUS", p.Line, p.File, p.Template.ToString(), p.Conditions);
        }

        private void EmitEfUsage(SyntaxNode scope, string methodId)
        {
            if (_catalog is null || _dbSetEntity.Count == 0) return;

            foreach (var member in scope.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
            {
                string propName = member.Name.Identifier.ValueText;
                if (!_dbSetEntity.TryGetValue(propName, out var entity)) continue;
                if (!_entityTable.TryGetValue(entity, out var tableId)) continue;

                bool write = member.Parent is MemberAccessExpressionSyntax
                {
                    Name.Identifier.ValueText: { } next
                } && EfWriteMethods.Contains(next);

                var props = new Dictionary<string, object> { ["via"] = "ef", ["entity"] = entity, ["line"] = Line(member) };

                // LINQ predicates over the DbSet in the same statement are business
                // rules governing this read/write (Where/First/Single/Any/Last...).
                var statement = member.Ancestors().OfType<StatementSyntax>().FirstOrDefault();
                if (statement is not null)
                {
                    var predicates = statement.DescendantNodes().OfType<InvocationExpressionSyntax>()
                        .Where(i => i.Expression is MemberAccessExpressionSyntax
                        {
                            Name.Identifier.ValueText: "Where" or "First" or "FirstOrDefault" or "Single"
                                or "SingleOrDefault" or "Any" or "Last" or "LastOrDefault" or "Count"
                        })
                        .SelectMany(i => i.ArgumentList.Arguments)
                        .Select(a => a.Expression)
                        .OfType<AnonymousFunctionExpressionSyntax>()
                        .Select(l => l.ToString())
                        .Distinct()
                        .ToList();
                    if (predicates.Count > 0)
                    {
                        foreach (var expression in predicates)
                        {
                            string ruleId = $"app::rule:{Hash8(methodId + "|" + expression)}";
                            AddNode(ruleId, "Rule", new() { ["type"] = "LINQ", ["expression"] = expression });
                            AddEdge("GOVERNS", ruleId, methodId);
                        }
                        props["conditions"] = string.Join(" AND ", predicates);
                    }
                }

                AddEdge(write ? "WRITES_TO" : "READS_FROM", methodId, tableId, props);
            }
        }

        // ---------------- rules (business logic governing a SQL call) ----------------
        private string? GoverningConditions(SyntaxNode site, string methodId)
        {
            var conditions = new List<string>();
            foreach (var ancestor in site.Ancestors())
            {
                switch (ancestor)
                {
                    case MethodDeclarationSyntax:
                        goto done;
                    case IfStatementSyntax ifs when !ifs.Condition.Span.Contains(site.Span):
                        bool inElse = ifs.Else is not null && ifs.Else.Span.Contains(site.Span);
                        conditions.Add(inElse ? $"NOT ({ifs.Condition})" : ifs.Condition.ToString());
                        break;
                    case SwitchStatementSyntax sw:
                        var section = sw.Sections.FirstOrDefault(s => s.Span.Contains(site.Span));
                        if (section is not null)
                            conditions.Add($"{sw.Expression} matches {string.Join("|", section.Labels.Select(l => l.ToString().TrimEnd(':')))}");
                        break;
                    case ConditionalExpressionSyntax cond when !cond.Condition.Span.Contains(site.Span):
                        bool inFalse = cond.WhenFalse.Span.Contains(site.Span);
                        conditions.Add(inFalse ? $"NOT ({cond.Condition})" : cond.Condition.ToString());
                        break;
                    case WhileStatementSyntax w:
                        conditions.Add(w.Condition.ToString());
                        break;
                }
            }
            done:
            if (conditions.Count == 0) return null;

            foreach (var expression in conditions)
            {
                string ruleId = $"app::rule:{Hash8(methodId + "|" + expression)}";
                AddNode(ruleId, "Rule", new() { ["type"] = "IF", ["expression"] = expression });
                AddEdge("GOVERNS", ruleId, methodId);
            }
            return string.Join(" AND ", conditions);
        }

        // ---------------- helpers ----------------
        private void AddBridge(string methodId, string targetId, string kind, string confidence, int line,
            string file, string matched, string? conditions)
        {
            // Same uniform boundary contract as any other protocol; "kind" and
            // "matched_literal" stay for the SQL-side consumers that already read them.
            string resolution = confidence switch
            {
                "EXTRACTED" => "literal",
                "RESOLVED" => "interproc_1",
                "AMBIGUOUS" => "catalog_match",
                _ => "unresolved",
            };
            var props = Boundary.Props("sql", confidence, resolution, matched, line, file);
            props["kind"] = kind;
            props["matched_literal"] = matched;
            if (conditions is not null) props["conditions"] = conditions;
            AddEdge("EXECUTES_SQL", methodId, targetId, props, dedupeKeyExtra: confidence + "|" + line);
        }

        /// <summary>
        /// Composition without a container: `IFoo foo = new Foo();`. Desktop, batch and
        /// pre-DI code wires itself this way, so binding only through DI registrations
        /// would leave those solutions with a call graph that dies at every interface.
        /// </summary>
        private void CollectNewBindings(SemanticModel model, SyntaxNode root)
        {
            foreach (var creation in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
            {
                var info = model.GetTypeInfo(creation);
                if (info.Type is not INamedTypeSymbol impl || !impl.Locations.Any(l => l.IsInSource)) continue;
                if (info.ConvertedType is not INamedTypeSymbol { TypeKind: TypeKind.Interface } iface) continue;
                if (!iface.Locations.Any(l => l.IsInSource)) continue;

                var key = (INamedTypeSymbol)iface.OriginalDefinition;
                if (!_newBindings.TryGetValue(key, out var list))
                    _newBindings[key] = list = new List<INamedTypeSymbol>();
                if (!list.Contains(impl, SymbolEqualityComparer.Default))
                    list.Add(impl);
            }
        }

        private void RecordDiBinding(INamedTypeSymbol iface, INamedTypeSymbol impl)
        {
            // Keyed on the open generic so IRepo<Order> registrations answer for
            // IRepo<T> call sites; closed-generic precision is out of scope in v1.
            var key = (INamedTypeSymbol)iface.OriginalDefinition;
            if (!_diBindings.TryGetValue(key, out var list))
                _diBindings[key] = list = new List<INamedTypeSymbol>();
            if (!list.Contains(impl, SymbolEqualityComparer.Default))
                list.Add(impl);
        }

        /// <summary>
        /// Walks every call that landed on an interface member through to the member
        /// that actually runs. Without this the call graph dies at the first injected
        /// interface and no controller -> service -> repository -> SQL chain closes.
        /// The implementation is chosen from the DI registration ("di") or, failing
        /// that, from a single implementation in the solution ("unique_impl"). Several
        /// candidates and no registration is left unresolved: guessing would put a
        /// wrong method on the impact path, which is worse than a gap.
        /// </summary>
        private void ResolveInterfaceCalls()
        {
            foreach (var call in _pendingIfaceCalls)
            {
                if (!_ifaceImpls.TryGetValue(call.Member, out var candidates) || candidates.Count == 0) continue;

                var iface = (INamedTypeSymbol)call.Member.ContainingType.OriginalDefinition;
                List<(INamedTypeSymbol Type, IMethodSymbol Member)> chosen;
                string resolution;

                if (_diBindings.TryGetValue(iface, out var bound))
                {
                    chosen = candidates.Where(c => bound.Contains(c.Type, SymbolEqualityComparer.Default)).ToList();
                    resolution = "di";
                    if (chosen.Count == 0) continue;
                }
                else if (candidates.Count == 1)
                {
                    chosen = candidates;
                    resolution = "unique_impl";
                }
                // A single type instantiated for this interface anywhere in the solution.
                // Several different ones is a data-flow question, and guessing it would
                // put a method that never runs on the impact path.
                else if (_newBindings.TryGetValue(iface, out var built) && built.Count == 1)
                {
                    chosen = candidates.Where(c => built.Contains(c.Type, SymbolEqualityComparer.Default)).ToList();
                    resolution = "new_binding";
                    if (chosen.Count == 0) continue;
                }
                else
                {
                    continue;
                }

                foreach (var (_, member) in chosen)
                {
                    if (!_methodIds.TryGetValue(member, out var implId)) continue;
                    // No dedupe key: a direct call to the same method already covers it.
                    AddEdge("CALLS", call.CallerId, implId, new()
                    {
                        ["via"] = "interface",
                        ["interface"] = $"app::{Fqn(iface)}.{call.Member.Name}",
                        ["resolution"] = resolution,
                        ["line"] = call.Line,
                        ["file"] = call.File,
                    });
                }
            }
        }

        /// <summary>
        /// A DLL analysed on its own has no Main and no routes, so every chain inside it
        /// would be orphaned. Its public methods that nothing in the solution calls are
        /// the way in — that is what a caller outside the solution can reach.
        /// </summary>
        private void EmitLibraryApiEntryPoints()
        {
            var called = _edges.Where(e => e.Type == "CALLS").Select(e => e.EndNodeId).ToHashSet(StringComparer.Ordinal);

            foreach (var candidate in _libraryApiCandidates)
            {
                if (called.Contains(candidate.MethodId)) continue;
                AddEntryPoint("library_api", candidate.Key, candidate.ClassId, candidate.MethodId,
                    candidate.Line, candidate.File);
            }
        }

        /// <summary>
        /// Rolls the call graph up into the levels an architect actually asks about.
        /// Class coupling so far came only from inheritance and constructor injection,
        /// which misses every static call and every collaborator obtained some other
        /// way; here it is derived from the calls that really happen, then aggregated
        /// into namespace-to-namespace and project-to-project dependencies.
        ///
        /// The declared project references are marked used/unused against that: a
        /// reference nobody exercises is a finding, not noise.
        /// </summary>
        private void EmitArchitectureDependencies()
        {
            // classId -> classId, counting the calls that back the dependency.
            var classUses = new Dictionary<(string From, string To), int>();

            foreach (var edge in _edges.Where(e => e.Type == "CALLS").ToList())
            {
                if (!_methodOwner.TryGetValue(edge.StartNodeId, out var fromClass)) continue;
                if (!_methodOwner.TryGetValue(edge.EndNodeId, out var toClass)) continue;
                if (fromClass == toClass) continue;

                var key = (fromClass, toClass);
                classUses[key] = classUses.TryGetValue(key, out var n) ? n + 1 : 1;
            }

            foreach (var ((fromClass, toClass), count) in classUses)
            {
                var props = new Dictionary<string, object> { ["kind"] = "uses", ["calls"] = count };
                if (CrossProject(fromClass, toClass, out var fromProj, out var toProj))
                {
                    props["cross_project"] = true;
                    props["from_project"] = fromProj;
                    props["to_project"] = toProj;
                }
                AddEdge("DEPENDS_ON", fromClass, toClass, props, dedupeKeyExtra: "uses");
            }

            // Namespace level: every class-to-class dependency, whatever produced it.
            var nsUses = new Dictionary<(string From, string To), int>();
            foreach (var edge in _edges.Where(e => e.Type == "DEPENDS_ON").ToList())
            {
                if (!_classNamespace.TryGetValue(edge.StartNodeId, out var fromNs)) continue;
                if (!_classNamespace.TryGetValue(edge.EndNodeId, out var toNs)) continue;
                if (fromNs == toNs) continue;

                var key = (fromNs, toNs);
                nsUses[key] = nsUses.TryGetValue(key, out var n) ? n + 1 : 1;
            }

            foreach (var ((fromNs, toNs), count) in nsUses)
                AddEdge("DEPENDS_ON", $"app::ns:{fromNs}", $"app::ns:{toNs}",
                    new() { ["kind"] = "namespace", ["references"] = count }, dedupeKeyExtra: "namespace");

            MarkUnusedProjectReferences(classUses.Keys);
        }

        private bool CrossProject(string fromClass, string toClass, out string fromProj, out string toProj)
        {
            fromProj = _classProject.TryGetValue(fromClass, out var f) ? f : "";
            toProj = _classProject.TryGetValue(toClass, out var t) ? t : "";
            return fromProj.Length > 0 && toProj.Length > 0 && fromProj != toProj;
        }

        /// <summary>
        /// A ProjectReference the code never exercises is dead weight in the build and a
        /// lie in the architecture diagram. The flag is only trustworthy because the
        /// usage side comes from resolved calls, so it is reported, never removed.
        /// </summary>
        private void MarkUnusedProjectReferences(IEnumerable<(string From, string To)> classUses)
        {
            var usedPairs = new HashSet<(string, string)>();
            foreach (var (fromClass, toClass) in classUses)
                if (CrossProject(fromClass, toClass, out var fromProj, out var toProj))
                    usedPairs.Add((fromProj, toProj));

            foreach (var edge in _edges)
            {
                if (edge.Type != "DEPENDS_ON") continue;
                if (!edge.Properties.TryGetValue("kind", out var kind) || (string)kind != "project") continue;

                string from = edge.StartNodeId["app::proj:".Length..];
                string to = edge.EndNodeId["app::proj:".Length..];
                edge.Properties["used"] = usedPairs.Contains((from, to));
            }
        }

        private void AddNode(string id, string label, Dictionary<string, object> props)
        {
            if (_nodes.ContainsKey(id)) return;
            props["name"] = props.TryGetValue("name", out var n) ? n : id;
            _nodes[id] = new GraphNode { Id = id, Labels = new List<string> { label }, Properties = props };
        }

        private void AddEdge(string type, string from, string to, Dictionary<string, object>? props = null,
            string? dedupeKeyExtra = null)
        {
            string key = $"{type}|{from}|{to}|{dedupeKeyExtra}";
            if (!_edgeKeys.Add(key)) return;
            _edges.Add(new GraphRel { Type = type, StartNodeId = from, EndNodeId = to, Properties = props ?? new() });
        }

        private string Rel(string path)
        {
            var rel = Path.GetRelativePath(_sln.RootDir, path);
            return rel.Replace('\\', '/');
        }

        private static string Fqn(INamedTypeSymbol type)
        {
            // Generic types keep their arity in the id (List`1-style) so
            // Repo<T> and a non-generic Repo never collide.
            string name = type.Arity > 0 ? $"{type.Name}`{type.Arity}" : type.Name;
            var ns = type.ContainingNamespace?.ToDisplayString();
            return string.IsNullOrEmpty(ns) || ns == "<global namespace>" ? name : $"{ns}.{name}";
        }

        private static bool InheritsDbContext(TypeDeclarationSyntax typeDecl) =>
            typeDecl.BaseList?.Types.Any(t => t.Type.ToString().Split('.').Last().Contains("DbContext")) == true;

        private static bool TypeNameEndsWith(TypeSyntax type, string suffix) =>
            type.ToString().Split('.').Last() == suffix;

        private static string? ConstString(ExpressionSyntax expr) =>
            expr is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.StringLiteralExpression)
                ? lit.Token.ValueText : null;

        private static int Line(SyntaxNode node) =>
            node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

        private static string Hash8(string value)
        {
            var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(value));
            return Convert.ToHexString(bytes)[..8].ToLowerInvariant();
        }

        private static void Record<TKey>(Dictionary<TKey, HashSet<string>> map, TKey key, string value)
            where TKey : notnull
        {
            if (!map.TryGetValue(key, out var set))
                map[key] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            set.Add(value);
        }

        private sealed class CallSiteKeyComparer : IEqualityComparer<(IMethodSymbol Method, int Ordinal)>
        {
            public bool Equals((IMethodSymbol Method, int Ordinal) x, (IMethodSymbol Method, int Ordinal) y) =>
                x.Ordinal == y.Ordinal && SymbolEqualityComparer.Default.Equals(x.Method, y.Method);

            public int GetHashCode((IMethodSymbol Method, int Ordinal) obj) =>
                HashCode.Combine(SymbolEqualityComparer.Default.GetHashCode(obj.Method), obj.Ordinal);
        }
    }
}
