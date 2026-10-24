namespace Parser.Mcp;

/// <summary>Un prompt MCP: plantilla de conversación que encadena herramientas del registro.</summary>
public sealed record McpPrompt(
    string Name,
    string Description,
    IReadOnlyList<McpPromptArg> Arguments,
    IReadOnlyList<string> Tools,
    Func<IReadOnlyDictionary<string, string>, string> Render);

public sealed record McpPromptArg(string Name, string Description, bool Required);

/// <summary>Registro de prompts. Cada uno declara las herramientas que cita; un gate las contrasta con McpToolRegistry.</summary>
public static class McpPrompts
{
    public static IReadOnlyList<McpPrompt> Default { get; } =
    [
        new McpPrompt(
            "onboard_database",
            "Orient yourself in an unfamiliar store: freshness, top risks, what the engine cannot see, and a summary for a human.",
            [],
            ["store_info", "risks", "blind_spots"],
            _ =>
                "Orient yourself in this lineage store. State only what the tools return; if a call returns nothing, say so instead of concluding there is nothing.\n" +
                "1. Call store_info. Report database, size (nodes/edges) and generated_at. If dias_desde_generado is high or an aviso is present, warn that the store may describe an outdated database.\n" +
                "2. Call risks (start with severity='high') and note the top findings by severity.\n" +
                "3. Call blind_spots and list what the engine does NOT see (e.g. dynamic SQL that never resolved). Say it explicitly.\n" +
                "4. Write a short summary for a human: store size, the most critical objects, the main risks, and the limits of this view."),

        new McpPrompt(
            "assess_change",
            "Assess what a planned change to an object, table or column would break, in what order to review it, and what stays unknown.",
            [new McpPromptArg("object", "Name of the object, table or column to be changed (e.g. dbo.Orders or dbo.Orders.Status).", true)],
            ["resolve_object", "describe_object", "impact", "column_impact", "evidence", "risks", "blind_spots"],
            a =>
                $"Assess the impact of changing: {a["object"]}. Claim nothing without evidence from the tools.\n" +
                "1. Call resolve_object with name set to that value to get the canonical id. If the name is ambiguous, list the candidates and ask which one.\n" +
                "2. Call describe_object with that id (for a SqlObject) to see what it reads, writes and calls.\n" +
                "3. Call impact with the id (direction 'downstream' for what breaks). If the id is a column, also call column_impact.\n" +
                "4. For each edge that backs an important claim, call evidence with object_id and target and cite the object and line_no.\n" +
                "5. Call risks and keep the findings that concern this object or its dependents.\n" +
                "6. Conclude with a verdict: what breaks, in what order to review it, and what remains unknown (call blind_spots; unresolved dynamic SQL means impact may be incomplete, never 'no impact')."),
    ];
}
