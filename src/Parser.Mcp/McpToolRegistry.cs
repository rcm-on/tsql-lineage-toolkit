namespace Parser.Mcp;

/// <summary>
/// Composition root de las herramientas. Una herramienta nueva se añade aquí y en ningún
/// otro sitio: tools/list y tools/call salen los dos de esta lista.
/// </summary>
public static class McpToolRegistry
{
    public static IReadOnlyList<IMcpTool> Default { get; } =
    [
        new ResolveObjectTool(),
        new ImpactTool(),
        new ColumnProvenanceTool(),
        new ColumnImpactTool(),
        new StoreInfoTool(),
        new DescribeObjectTool(),
    ];
}
