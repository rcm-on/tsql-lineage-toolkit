// TSqlParser: ScriptDom-based AST parser for T-SQL stored procedures / functions / triggers.
//
// Reads a JSON array of { "name": "Database::Schema.Object", "sql": "CREATE PROCEDURE ..." }
// and writes a graph (nodes + relationships) in the same shape as
// src/neo4j_exporter.py's "rule engine" subgraph: SqlObject -> Step -> Action,
// Rule -> GOVERNS -> Step, SqlObject -CALLS-> SqlObject, plus Variable nodes.
//
// Usage:
//   dotnet run -- input.json output_graph.json [output_workflows.json] [--columns] [--graphify]
//
// --columns: also emit :Column nodes (HAS_COLUMN / READS_COLUMN / WRITES_COLUMN).
// --graphify: also write "<output_graph>.graphify.json" in the flat
//   { meta, stats, nodes, edges } shape (src/exporter.py-compatible) for Graphify
//   / D3 / vis-network (viewer.html) - convertible to Cypher for Neo4j.
// --graphml: also write "<output_graph>.graphml" (graph XML) for Gephi / yEd /
//   Cytoscape / NetworkX.
// Off by default - column lists come only from the SQL text (INSERT column
// list, UPDATE SET clauses, SELECT list) and have no data type, so this is an
// opt-in level of detail rather than a full schema.

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TSqlParser;

var positional = args.Where(a => !a.StartsWith("--")).ToList();
var includeColumns = args.Contains("--columns");
var emitGraphify = args.Contains("--graphify");
var emitGraphml = args.Contains("--graphml");

// "validate <graph.json> [--server <server>]": cross-checks FK_TO/CALLS edges
// in an already-built graph against the live database (sys.foreign_keys,
// sys.sql_expression_dependencies) - read-only, no graph regenerated.
if (positional.Count >= 1 && positional[0] == "validate")
{
    if (positional.Count < 2)
    {
        Console.Error.WriteLine("Usage: TSqlParser validate <graph.json> [--server <server>]");
        return 1;
    }
    var serverArgIdx = Array.IndexOf(args, "--server");
    var server = serverArgIdx >= 0 && serverArgIdx + 1 < args.Length ? args[serverArgIdx + 1] : @".\SQLEXPRESS";
    return DbValidator.Run(positional[1], server);
}

// "extract <database> <input.json> [--server <server>] [--tables] [--object schema.name]... [--like pattern]":
// connects to a live database and dumps the SQL definitions of its
// procedures/functions/triggers/views (sys.sql_modules) into input.json,
// ready for the main pipeline below.
//   --object schema.name : restrict to this object (repeatable for several).
//   --like pattern        : restrict to objects matching this T-SQL LIKE
//                            pattern over "schema.name" (e.g. "dbo.usp_Sales%").
//   --tables              : also append CREATE TABLE DDL for every base table
//                            (sys.tables) so input.json is self-contained.
if (positional.Count >= 1 && positional[0] == "extract")
{
    if (positional.Count < 3)
    {
        Console.Error.WriteLine("Usage: TSqlParser extract <database> <input.json> [--server <server>] [--tables] [--object schema.name]... [--like pattern]");
        return 1;
    }
    var serverArgIdx = Array.IndexOf(args, "--server");
    var server = serverArgIdx >= 0 && serverArgIdx + 1 < args.Length ? args[serverArgIdx + 1] : @".\SQLEXPRESS";
    var likeArgIdx = Array.IndexOf(args, "--like");
    var likePattern = likeArgIdx >= 0 && likeArgIdx + 1 < args.Length ? args[likeArgIdx + 1] : null;
    var objectNames = new List<string>();
    for (var i = 0; i < args.Length - 1; i++)
        if (args[i] == "--object")
            objectNames.Add(args[i + 1]);

    var extractResult = ObjectExtractor.Run(positional[1], positional[2], server, objectNames, likePattern);
    if (extractResult != 0 || !args.Contains("--tables"))
        return extractResult;
    return TableSchemaExtractor.RunAll(positional[1], positional[2], server);
}

// "extract-tables <graph.json> <input.json> [--server <server>]": for every
// :Table node in graph.json, fetches its CREATE TABLE DDL (columns, types,
// PK, FK) from the live database and appends it to input.json.
if (positional.Count >= 1 && positional[0] == "extract-tables")
{
    if (positional.Count < 3)
    {
        Console.Error.WriteLine("Usage: TSqlParser extract-tables <graph.json> <input.json> [--server <server>]");
        return 1;
    }
    var serverArgIdx = Array.IndexOf(args, "--server");
    var server = serverArgIdx >= 0 && serverArgIdx + 1 < args.Length ? args[serverArgIdx + 1] : @".\SQLEXPRESS";
    return TableSchemaExtractor.Run(positional[1], positional[2], server);
}

// "report <input.json> [nombre-objeto]": informe en texto, sin grafo ni JSON.
//   - sin nombre  -> informe general de la base de datos.
//   - con nombre  -> informe detallado de ese SP/funcion/trigger (parametros,
//     variables, referencias y grafo de control con anidamiento).
// El nombre puede ser parcial (busca por "contiene", case-insensitive).
if (positional.Count >= 1 && positional[0] == "report")
{
    if (positional.Count < 2)
    {
        Console.Error.WriteLine("Usage: TSqlParser report <input.json> [nombre-objeto]");
        return 1;
    }
    var (repResults, repTables) = AnalyzeInput(positional[1]);
    if (positional.Count >= 3)
    {
        var needle = positional[2];
        var matches = repResults.Where(r => r.ObjectName.Contains(needle, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0)
        {
            Console.Error.WriteLine($"No se encontro ningun objeto que contenga '{needle}'.");
            return 1;
        }
        if (matches.Count > 1)
        {
            Console.Error.WriteLine($"'{needle}' es ambiguo, coincide con {matches.Count} objetos:");
            foreach (var m in matches.Take(20))
                Console.Error.WriteLine($"  {m.ObjectName}");
            return 1;
        }
        Console.WriteLine(ReportGenerator.ObjectReport(matches[0]));
    }
    else
    {
        Console.WriteLine(ReportGenerator.GeneralReport(repResults, repTables));
    }
    return 0;
}

if (positional.Count < 2)
{
    Console.Error.WriteLine("Usage: TSqlParser <input.json> <output_graph.json> [output_workflows.json] [--columns] [--graphify]");
    Console.Error.WriteLine("       TSqlParser report <input.json> [nombre-objeto]");
    Console.Error.WriteLine("       TSqlParser extract <database> <input.json> [--server <server>] [--tables] [--object schema.name]... [--like pattern]");
    Console.Error.WriteLine("       TSqlParser validate <graph.json> [--server <server>]");
    Console.Error.WriteLine("       TSqlParser extract-tables <graph.json> <input.json> [--server <server>]");
    return 1;
}

var inputPath = positional[0];
var graphOutputPath = positional[1];
var workflowsOutputPath = positional.Count > 2 ? positional[2] : null;

var jsonOptions = new JsonSerializerOptions
{
    WriteIndented = true,
    PropertyNameCaseInsensitive = true,
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
};

var (results, tableSchemas) = AnalyzeInput(inputPath);

if (workflowsOutputPath != null)
    File.WriteAllText(workflowsOutputPath, JsonSerializer.Serialize(results, jsonOptions), Encoding.UTF8);

var graph = GraphExporter.Build(results, includeColumns, tableSchemas);
File.WriteAllText(graphOutputPath, JsonSerializer.Serialize(graph, jsonOptions), Encoding.UTF8);

// --graphify: also emit the flat { meta, stats, nodes, edges } shape that
// src/exporter.py produces, so the same graph loads into Graphify (which can
// itself convert nodes+edges -> Cypher for Neo4j). Written alongside the Neo4j
// output as "<graphOutputPath without .json>.graphify.json".
if (emitGraphify)
{
    var db = results.Select(o => o.ObjectName.Split("::", 2)).FirstOrDefault(p => p.Length == 2)?[0] ?? "";
    var graphify = GraphifyExporter.ToGraphify(graph, db);
    var graphifyPath = graphOutputPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
        ? graphOutputPath[..^5] + ".graphify.json"
        : graphOutputPath + ".graphify.json";
    File.WriteAllText(graphifyPath, JsonSerializer.Serialize(graphify, jsonOptions), Encoding.UTF8);
    Console.WriteLine($"Graphify: {graphify.Nodes.Count} nodes, {graphify.Edges.Count} edges -> {graphifyPath}");
}

// --graphml: also emit GraphML (graph XML) for Gephi / yEd / Cytoscape, written
// as "<graphOutputPath without .json>.graphml".
if (emitGraphml)
{
    var graphmlPath = graphOutputPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
        ? graphOutputPath[..^5] + ".graphml"
        : graphOutputPath + ".graphml";
    File.WriteAllText(graphmlPath, GraphMlExporter.ToGraphMl(graph), Encoding.UTF8);
    Console.WriteLine($"GraphML: {graph.Nodes.Count} nodes, {graph.Relationships.Count} edges -> {graphmlPath}");
}

var ok = results.Count(r => r.Error == null);
Console.WriteLine($"Analyzed {results.Count} objects ({ok} ok, {results.Count - ok} parse errors)");
if (tableSchemas.Count > 0)
{
    var tableOk = tableSchemas.Count(t => t.Error == null);
    Console.WriteLine($"Analyzed {tableSchemas.Count} table schemas ({tableOk} ok, {tableSchemas.Count - tableOk} errors)");
}
Console.WriteLine($"Graph: {graph.Nodes.Count} nodes, {graph.Relationships.Count} relationships -> {graphOutputPath}");
return 0;

// Two-pass analysis of an input.json (shared by the default graph build and the
// "report" command): CREATE TABLE definitions first (so their column lists are
// available to expand "SELECT *"), then procedures/functions/triggers.
(List<ObjectResult> Results, List<TableSchemaResult> TableSchemas) AnalyzeInput(string path)
{
    var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
    var sources = JsonSerializer.Deserialize<List<SourceObject>>(File.ReadAllText(path), opts)
        ?? throw new InvalidDataException("Could not parse input JSON");

    var createTableRegex = new Regex(@"^\s*CREATE\s+TABLE\b", RegexOptions.IgnoreCase);

    var schemas = new List<TableSchemaResult>();
    var objectSources = new List<SourceObject>();
    foreach (var src in sources)
    {
        if (createTableRegex.IsMatch(src.Sql))
            schemas.Add(TableAnalyzer.AnalyzeTable(src.Name, src.Sql));
        else
            objectSources.Add(src);
    }

    var cols = new Dictionary<string, List<string>>();
    foreach (var schema in schemas)
    {
        if (schema.Error != null)
            continue;
        var parts = schema.ObjectName.Split("::", 2);
        if (parts.Length != 2)
            continue;
        cols[$"{parts[0]}::{SqlText.NormalizeRef(parts[1])}"] = schema.Columns.Select(c => c.Name).ToList();
    }

    var objResults = new List<ObjectResult>();
    foreach (var src in objectSources)
        objResults.Add(SqlAnalyzer.AnalyzeObject(src.Name, src.Sql, cols));

    return (objResults, schemas);
}
