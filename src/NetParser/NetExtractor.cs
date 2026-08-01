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
///                CONTAINS, DEPENDS_ON, CALLS (semantic, intra-solution)
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

    private static List<MetadataReference>? _runtimeRefs;

    /// <summary>
    /// Optional nodestore model.json path with the SQL catalog. Without it the
    /// extractor still emits app structure, but no bridge edges.
    /// </summary>
    public string? CatalogPath { get; set; }

    /// <summary>Also emit AMBIGUOUS candidate edges (catalog matches of an unresolved template).</summary>
    public bool IncludeAmbiguous { get; set; } = true;

    public string Name => "net";

    public bool CanHandle(string inputPath) =>
        inputPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
        || inputPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
        || (Directory.Exists(inputPath) &&
            (Directory.EnumerateFiles(inputPath, "*.sln", SearchOption.AllDirectories).Any() ||
             Directory.EnumerateFiles(inputPath, "*.csproj", SearchOption.AllDirectories).Any()));

    public GraphPayload Extract(string inputPath)
    {
        var sln = SolutionLoader.Load(inputPath);
        var catalog = CatalogPath is not null ? Catalog.Load(CatalogPath) : null;
        var b = new Builder(sln, catalog, IncludeAmbiguous);
        return b.Build();
    }

    private static IReadOnlyList<MetadataReference> RuntimeRefs()
    {
        if (_runtimeRefs is null)
        {
            _runtimeRefs = new List<MetadataReference>();
            var tpa = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "";
            foreach (var path in tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    _runtimeRefs.Add(MetadataReference.CreateFromFile(path));
        }
        return _runtimeRefs;
    }

    // ------------------------------------------------------------------
    private sealed class Builder
    {
        private readonly SolutionInfo _sln;
        private readonly Catalog? _catalog;
        private readonly bool _includeAmbiguous;

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
        // DbSet property name -> entity type name (per solution; names are enough at this scale)
        private readonly Dictionary<string, string> _dbSetEntity = new(StringComparer.Ordinal);
        // entity type name -> resolved table id
        private readonly Dictionary<string, string> _entityTable = new(StringComparer.Ordinal);

        private sealed record PendingTemplate(
            string MethodId, SqlStringTemplate Template, bool IsProcName, int Line, string File, string? Conditions);

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

                compilations[proj.Name] = CSharpCompilation.Create(proj.Name, projTrees, refs,
                    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            }

            // Pass 1: declarations (classes, methods, CONTAINS) + EF model mapping.
            foreach (var comp in compilations.Values)
                foreach (var tree in comp.SyntaxTrees)
                    DeclarationPass(comp.GetSemanticModel(tree), tree, trees[tree].FileId);

            // Pass 2: calls + call-site literals, SQL sinks, EF usage, rules.
            foreach (var comp in compilations.Values)
                foreach (var tree in comp.SyntaxTrees)
                    BodyPass(comp.GetSemanticModel(tree), tree);

            // Pass 3: narrow pending templates from call-site literals.
            foreach (var pending in _pending)
                EmitTemplate(pending);

            int i = 0;
            foreach (var e in _edges) e.Id = $"a{i++}";
            var payload = new GraphPayload();
            payload.Nodes.AddRange(_nodes.Values);
            payload.Relationships.AddRange(_edges);
            return payload;
        }

        // ---------------- pass 1: declarations + EF model ----------------
        private void DeclarationPass(SemanticModel model, SyntaxTree tree, string fileId)
        {
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
                }

                CollectEfModel(typeDecl, typeSymbol, model);
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
            string endpointId = $"app::endpoint:{verb} {route}";
            AddNode(endpointId, "AppEndpoint", new()
            {
                ["name"] = $"{verb} {route}", ["verb"] = verb, ["route"] = route,
                ["path"] = file, ["line"] = line,
            });
            if (ownerId is not null) AddEdge("EXPOSES", ownerId, endpointId);
            AddEdge("CALLS", endpointId, handlerId);
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
        private void BodyPass(SemanticModel model, SyntaxTree tree)
        {
            var root = tree.GetRoot();

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
                    string mainId = $"app::{((CSharpCompilation)model.Compilation).AssemblyName}.Program.Main";
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
                    AddEdge("DEPENDS_ON", $"app::{Fqn(iface)}", $"app::{Fqn(impl)}", new()
                    {
                        ["kind"] = "di",
                        ["lifetime"] = gen.Identifier.ValueText["Add".Length..].ToLowerInvariant(),
                        ["registered_in"] = callerId,
                        ["line"] = Line(inv),
                    });
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
                    if (url.HasKnownText)
                    {
                        string urlText = url.Literal ?? url.ToString();
                        string svc = Uri.TryCreate(urlText, UriKind.Absolute, out var abs) ? abs.Host : urlText;
                        string svcId = $"app::svc:{svc}";
                        AddNode(svcId, "ExternalService", new() { ["name"] = svc, ["url"] = urlText });
                        AddEdge("CALLS", callerId, svcId, new() { ["kind"] = "http", ["url"] = urlText, ["line"] = Line(inv) });
                    }
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
            var props = new Dictionary<string, object>
            {
                ["kind"] = kind,
                ["confidence"] = confidence,
                ["line"] = line,
                ["file"] = file,
                ["matched_literal"] = matched,
            };
            if (conditions is not null) props["conditions"] = conditions;
            AddEdge("EXECUTES_SQL", methodId, targetId, props, dedupeKeyExtra: confidence + "|" + line);
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
