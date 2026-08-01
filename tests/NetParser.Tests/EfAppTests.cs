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
        Assert.Contains(G.Nodes, n => n.Id == "app::endpoint:GET /api/orders/big");
        Assert.NotNull(Edge("EXPOSES", "app::EfApp.OrdersController", "app::endpoint:GET /api/orders/big"));
        Assert.NotNull(Edge("CALLS", "app::endpoint:GET /api/orders/big", "app::EfApp.OrdersController.GetBigOrders"));
        Assert.Contains(G.Nodes, n => n.Id == "app::endpoint:POST /api/orders");
    }

    [Fact]
    public void Minimal_api_route_is_captured() =>
        Assert.Contains(G.Nodes, n => n.Id == "app::endpoint:GET /health");

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
    public void Outbound_http_call_becomes_external_service()
    {
        Assert.Contains(G.Nodes, n => n.Labels.Contains("ExternalService") && n.Id == "app::svc:billing.internal");
        Assert.NotNull(Edge("CALLS", "app::EfApp.OrdersController.CreateOrder", "app::svc:billing.internal"));
    }

    [Fact]
    public void Project_kind_and_databases_come_from_csproj_and_appsettings()
    {
        var proj = G.Nodes.Single(n => n.Labels.Contains("AppProject"));
        Assert.Equal("web", (string)proj.Properties["kind"]);
        Assert.Contains("WideWorldImporters", (List<string>)proj.Properties["databases"]);
    }

    [Fact]
    public void Structure_contains_chain_is_complete()
    {
        Assert.Contains(G.Nodes, n => n.Labels.Contains("AppSolution"));
        Assert.Contains(Edges("CONTAINS"), r => r.EndNodeId == "app::EfApp.OrdersController");
        Assert.Contains(Edges("DEPENDS_ON"), r =>
            r.EndNodeId == "app::pkg:Dapper" && (string)r.Properties["kind"] == "package");
        // Calls through an injected interface resolve to the interface method;
        // the DI edge (IOrderRepository -> OrderRepository) closes the loop.
        Assert.Contains(Edges("CALLS"), r =>
            r.StartNodeId == "app::EfApp.OrdersController.GetBigOrders" &&
            r.EndNodeId == "app::EfApp.IOrderRepository.GetBigOrders");
    }
}
