using Parser.Contracts;

namespace NetParser.Tests;

/// <summary>
/// Gate del extractor sobre la fixture EfApp: EF (atributo/fluent/convención y
/// uso de DbSet), narrowing interprocedural del patrón D (RESOLVED), endpoints
/// Web API, interfaces/DI, reglas de negocio (IF y LINQ), servicios externos,
/// appsettings y clasificación de proyecto.
/// </summary>
public class EfAppTests
{
    private static readonly Lazy<GraphPayload> Payload = new(() => Fixtures.Extract("efapp"));

    private static GraphPayload G => Payload.Value;

    private static List<GraphRel> Edges(string type) =>
        G.Relationships.Where(r => r.Type == type).ToList();

    private static GraphRel? Edge(string type, string from, string to) =>
        G.Relationships.FirstOrDefault(r => r.Type == type && r.StartNodeId == from && r.EndNodeId == to);

    [Theory]
    [InlineData("app::EfApp.Customer", "WideWorldImporters:table:sales.customers")]   // [Table] attribute
    [InlineData("app::EfApp.OrderLine", "WideWorldImporters:table:sales.orderlines")] // fluent ToTable
    [InlineData("app::EfApp.Order", "WideWorldImporters:table:sales.orders")]         // DbSet convention
    public void Ef_entities_map_to_tables(string entity, string table) =>
        Assert.NotNull(Edge("MAPS_TO", entity, table));

    [Fact]
    public void DbSet_read_emits_READS_FROM_with_linq_rule()
    {
        var read = Edge("READS_FROM", "app::EfApp.OrderRepository.GetBigOrders", "WideWorldImporters:table:sales.orders");
        Assert.NotNull(read);
        Assert.Contains("o.Total > 100", (string)read!.Properties["conditions"]);

        var rule = G.Nodes.FirstOrDefault(n => n.Labels.Contains("Rule") &&
            n.Properties.TryGetValue("expression", out var e) && ((string)e).Contains("o.Total > 100"));
        Assert.NotNull(rule);
        Assert.NotNull(Edge("GOVERNS", rule!.Id, "app::EfApp.OrderRepository.GetBigOrders"));
    }

    [Fact]
    public void DbSet_add_emits_WRITES_TO() =>
        Assert.NotNull(Edge("WRITES_TO", "app::EfApp.OrderRepository.AddOrder", "WideWorldImporters:table:sales.orders"));

    [Theory]
    [InlineData("WideWorldImporters::Integration.GetOrderUpdates")]
    [InlineData("WideWorldImporters::Integration.GetSaleUpdates")]
    public void PatternD_with_literal_call_sites_resolves(string target)
    {
        var edge = Edge("EXECUTES_SQL", "app::EfApp.FeedRunner.Run", target);
        Assert.NotNull(edge);
        Assert.Equal("RESOLVED", (string)edge!.Properties["confidence"]);
    }

    [Fact]
    public void PatternD_narrowed_emits_no_ambiguous_candidates() =>
        Assert.DoesNotContain(Edges("EXECUTES_SQL"), r =>
            r.StartNodeId == "app::EfApp.FeedRunner.Run" &&
            (string)r.Properties["confidence"] == "AMBIGUOUS");

    [Fact]
    public void Guarded_dapper_call_yields_if_rule_and_table_edge()
    {
        var edge = Edge("EXECUTES_SQL", "app::EfApp.FeedRunner.Cleanup", "WideWorldImporters:table:sales.specialdeals");
        Assert.NotNull(edge);
        Assert.Equal("force", (string)edge!.Properties["conditions"]);
    }

    [Fact]
    public void Controller_actions_become_endpoints()
    {
        Assert.Contains(G.Nodes, n => n.Id == "app::entry:http_route:GET /api/orders/big");
        Assert.NotNull(Edge("EXPOSES", "app::EfApp.OrdersController", "app::entry:http_route:GET /api/orders/big"));
        Assert.NotNull(Edge("CALLS", "app::entry:http_route:GET /api/orders/big", "app::EfApp.OrdersController.GetBigOrders"));
        Assert.Contains(G.Nodes, n => n.Id == "app::entry:http_route:POST /api/orders");
    }

    [Fact]
    public void Minimal_api_route_is_captured() =>
        Assert.Contains(G.Nodes, n => n.Id == "app::entry:http_route:GET /health");

    [Fact]
    public void Interfaces_di_and_injection_are_linked()
    {
        Assert.NotNull(Edge("IMPLEMENTS", "app::EfApp.OrderRepository", "app::EfApp.IOrderRepository"));

        var di = Edge("DEPENDS_ON", "app::EfApp.IOrderRepository", "app::EfApp.OrderRepository");
        Assert.NotNull(di);
        Assert.Equal("di", (string)di!.Properties["kind"]);
        Assert.Equal("scoped", (string)di.Properties["lifetime"]);

        var injected = Edge("DEPENDS_ON", "app::EfApp.OrdersController", "app::EfApp.IOrderRepository");
        Assert.NotNull(injected);
        Assert.Equal("injected", (string)injected!.Properties["kind"]);
    }

    [Fact]
    public void Outbound_http_call_becomes_a_boundary_target()
    {
        string targetId = Boundary.TargetId("http", "billing.internal");
        Assert.Contains(G.Nodes, n => n.Labels.Contains(Boundary.TargetLabel) && n.Id == targetId);

        var edge = Edge(Boundary.ExternalEdge, "app::EfApp.OrdersController.CreateOrder", targetId);
        Assert.NotNull(edge);
        Assert.Equal("http", (string)edge!.Properties["protocol"]);
        Assert.Equal("EXTRACTED", (string)edge.Properties["confidence"]);
        Assert.Equal("literal", (string)edge.Properties["resolution"]);
    }

    /// <summary>
    /// The whole point of the uniform contract: "what infrastructure does this touch?"
    /// is one query over BoundaryEdgeTypes, not a list of per-protocol special cases.
    /// </summary>
    [Fact]
    public void Sql_and_http_share_the_boundary_property_contract()
    {
        var boundary = G.Relationships.Where(r => Vocab.BoundaryEdgeTypes.Contains(r.Type)).ToList();

        Assert.Contains(boundary, r => (string)r.Properties["protocol"] == "sql");
        Assert.Contains(boundary, r => (string)r.Properties["protocol"] == "http");
        Assert.All(boundary, r =>
        {
            Assert.Contains((string)r.Properties["protocol"], Boundary.Protocols);
            Assert.Contains((string)r.Properties["confidence"], Boundary.Confidences);
            Assert.Contains((string)r.Properties["resolution"], Boundary.Resolutions);
        });
    }

    [Fact]
    public void Project_kind_and_databases_come_from_csproj_and_appsettings()
    {
        var proj = G.Nodes.Single(n => n.Labels.Contains("AppProject") && (string)n.Properties["name"] == "EfApp");
        Assert.Equal("web", (string)proj.Properties["kind"]);
        Assert.Contains("WideWorldImporters", (List<string>)proj.Properties["databases"]);
    }

    /// <summary>
    /// A .NET estate is not only web APIs. A batch process declares itself in App.config
    /// and packages.config, which the SDK-style readers never look at.
    /// </summary>
    [Fact]
    public void Console_project_reads_appconfig_and_packages_config()
    {
        var proj = G.Nodes.Single(n => n.Labels.Contains("AppProject") && (string)n.Properties["name"] == "EfBatch");

        Assert.Equal("console", (string)proj.Properties["kind"]);
        Assert.Contains("WideWorldImportersDW", (List<string>)proj.Properties["databases"]);
        Assert.NotNull(Edge("DEPENDS_ON", "app::proj:EfBatch", "app::pkg:Dapper"));
    }

    [Theory]
    [InlineData("app::entry:console_main:Program.Main", "console_main")]              // scheduled process
    [InlineData("app::entry:hosted_service:NightlyWorker.ExecuteAsync", "hosted_service")] // service loop
    [InlineData("app::entry:library_api:XmlExporter.Export", "library_api")]          // DLL public surface
    public void Non_http_entry_points_are_captured(string id, string kind)
    {
        var node = G.Nodes.SingleOrDefault(n => n.Id == id);
        Assert.NotNull(node);
        Assert.Contains("EntryPoint", node!.Labels);
        Assert.Equal(kind, (string)node.Properties["kind"]);
        Assert.Contains(kind, Vocab.EntryPointKinds);
    }

    /// <summary>A public method something else in the solution calls is not a way in.</summary>
    [Fact]
    public void Library_api_entry_points_exclude_methods_called_inside_the_solution() =>
        Assert.DoesNotContain(G.Nodes, n => n.Id == "app::entry:library_api:CsvExporter.Export");

    /// <summary>
    /// The layer map: namespaces are nodes, and the dependency between them is rolled
    /// up from the calls that really happen — not from the using directives, which say
    /// what the file mentions rather than what the code does.
    /// </summary>
    [Fact]
    public void Namespaces_are_nodes_with_dependencies_between_them()
    {
        Assert.Contains(G.Nodes, n => n.Labels.Contains("AppNamespace") && n.Id == "app::ns:EfLib.Domain");
        Assert.NotNull(Edge("BELONGS_TO", "app::EfLib.Domain.ExportFormat", "app::ns:EfLib.Domain"));

        var nsDep = Edge("DEPENDS_ON", "app::ns:EfLib", "app::ns:EfLib.Domain");
        Assert.NotNull(nsDep);
        Assert.Equal("namespace", (string)nsDep!.Properties["kind"]);

        // Nothing in EfLib.Domain reaches back: the layering holds in this fixture.
        Assert.Null(Edge("DEPENDS_ON", "app::ns:EfLib.Domain", "app::ns:EfLib"));
    }

    /// <summary>
    /// Class coupling from the calls themselves, which is what catches a collaborator
    /// that is neither inherited nor injected.
    /// </summary>
    [Fact]
    public void Class_usage_dependencies_carry_the_project_they_cross()
    {
        var uses = Edge("DEPENDS_ON", "app::EfBatch.Program", "app::EfLib.ExportRunner");
        Assert.NotNull(uses);
        Assert.Equal("uses", (string)uses!.Properties["kind"]);
        Assert.True((bool)uses.Properties["cross_project"]);
        Assert.Equal("EfBatch", (string)uses.Properties["from_project"]);
        Assert.Equal("EfLib", (string)uses.Properties["to_project"]);
    }

    /// <summary>A ProjectReference nobody exercises is dead weight, and it is reportable.</summary>
    [Theory]
    [InlineData("app::proj:EfBatch", true)]   // Main calls into EfLib
    [InlineData("app::proj:EfApp", false)]    // declared, never touched
    public void Declared_project_references_are_marked_used_or_dead(string fromProject, bool used)
    {
        var edge = Edge("DEPENDS_ON", fromProject, "app::proj:EfLib");
        Assert.NotNull(edge);
        Assert.Equal("project", (string)edge!.Properties["kind"]);
        Assert.Equal(used, (bool)edge.Properties["used"]);
    }

    [Fact]
    public void Binary_assembly_reference_is_a_dependency()
    {
        var edge = Edge("DEPENDS_ON", "app::proj:EfLib", "app::asm:Legacy.Data");
        Assert.NotNull(edge);
        Assert.Equal("assembly", (string)edge!.Properties["kind"]);
    }

    /// <summary>Composition without a container: the `new` is the binding.</summary>
    [Fact]
    public void Interface_call_resolves_through_a_new_expression_when_there_is_no_container()
    {
        var resolved = Edge("CALLS", "app::EfLib.ExportRunner.RunExport", "app::EfLib.CsvExporter.Export");
        Assert.NotNull(resolved);
        Assert.Equal("new_binding", (string)resolved!.Properties["resolution"]);

        Assert.Null(Edge("CALLS", "app::EfLib.ExportRunner.RunExport", "app::EfLib.XmlExporter.Export"));
    }

    [Fact]
    public void Structure_contains_chain_is_complete()
    {
        Assert.Contains(G.Nodes, n => n.Labels.Contains("AppSolution"));
        Assert.Contains(Edges("CONTAINS"), r => r.EndNodeId == "app::EfApp.OrdersController");
        Assert.Contains(Edges("DEPENDS_ON"), r =>
            r.EndNodeId == "app::pkg:Dapper" && (string)r.Properties["kind"] == "package");
        // A call through an injected interface keeps the structural edge to the
        // interface member; ResolveInterfaceCalls adds the resolved one.
        Assert.Contains(Edges("CALLS"), r =>
            r.StartNodeId == "app::EfApp.OrdersController.GetBigOrders" &&
            r.EndNodeId == "app::EfApp.IOrderRepository.GetBigOrders");
    }

    [Fact]
    public void Interface_call_resolves_to_the_implementation_registered_in_di()
    {
        var resolved = Edge("CALLS",
            "app::EfApp.OrdersController.GetBigOrders", "app::EfApp.OrderRepository.GetBigOrders");

        Assert.NotNull(resolved);
        Assert.Equal("interface", (string)resolved!.Properties["via"]);
        Assert.Equal("di", (string)resolved.Properties["resolution"]);
        Assert.Equal("app::EfApp.IOrderRepository.GetBigOrders", (string)resolved.Properties["interface"]);
        Assert.Equal(21, Convert.ToInt32(resolved.Properties["line"]));
    }

    /// <summary>
    /// The point of resolving the interface: the endpoint reaches the table. Without
    /// it the walk stops at IOrderRepository and no app->SQL chain exists.
    /// </summary>
    [Fact]
    public void Endpoint_reaches_the_table_through_the_resolved_call()
    {
        var seen = new HashSet<string>();
        var queue = new Queue<string>();
        queue.Enqueue("app::entry:http_route:GET /api/orders/big");

        while (queue.Count > 0)
        {
            string current = queue.Dequeue();
            if (!seen.Add(current)) continue;
            foreach (var next in G.Relationships
                         .Where(r => r.StartNodeId == current && r.Type is "CALLS" or "READS_FROM" or "WRITES_TO")
                         .Select(r => r.EndNodeId))
                queue.Enqueue(next);
        }

        Assert.Contains("app::EfApp.OrderRepository.GetBigOrders", seen);
        Assert.Contains("WideWorldImporters:table:sales.orders", seen);
    }

    [Fact]
    public void Single_implementation_resolves_without_a_di_registration()
    {
        var resolved = Edge("CALLS",
            "app::EfApp.CheckoutService.Total", "app::EfApp.StandardDiscountPolicy.Apply");

        Assert.NotNull(resolved);
        Assert.Equal("unique_impl", (string)resolved!.Properties["resolution"]);
    }

    /// <summary>
    /// Two implementations and no registration: nothing is invented. A wrong method
    /// on the impact path costs more than a missing one.
    /// </summary>
    [Fact]
    public void Ambiguous_implementations_are_left_unresolved()
    {
        Assert.NotNull(Edge("CALLS", "app::EfApp.CheckoutService.Total", "app::EfApp.ITaxRule.Tax"));

        Assert.DoesNotContain(Edges("CALLS"), r =>
            r.StartNodeId == "app::EfApp.CheckoutService.Total" &&
            r.EndNodeId is "app::EfApp.SpainTaxRule.Tax" or "app::EfApp.PortugalTaxRule.Tax");
    }
}
