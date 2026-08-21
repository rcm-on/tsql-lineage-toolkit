// TSqlParser: ScriptDom-based AST parser for T-SQL stored procedures / functions / triggers.
//
// Reads a JSON array of { "name": "Database::Schema.Object", "sql": "CREATE PROCEDURE ..." }
// and writes a graph (nodes + relationships) in the same shape as
// src/neo4j_exporter.py's "rule engine" subgraph: SqlObject -> Step -> Action,
// Rule -> GOVERNS -> Step, SqlObject -CALLS-> SqlObject, plus Variable nodes.
//
// Usage:
//   dotnet run -- input.json output_graph.json [output_workflows.json] [--columns] [--graphify] [--graphml] [--nodestore] [--verify-audit] [--sqlite]
//
// --columns: also emit :Column nodes (HAS_COLUMN / READS_COLUMN / WRITES_COLUMN).
// --verify-audit: after writing --nodestore, validate audit_report.json invariants
//   (no empty by_type keys, coverage_pct in [0,100], hotspot scores > 0, valid
//   risk_pattern severities). Exits non-zero if any invariant fails. No-op without
//   --nodestore. Also applies to update-nodestore.
// --graphify: also write "<output_graph>.graphify.json" in the flat
//   { meta, stats, nodes, edges } shape (src/exporter.py-compatible) for Graphify
//   / D3 / vis-network (viewer.html) - convertible to Cypher for Neo4j.
// --graphml: also write "<output_graph>.graphml" (graph XML) for Gephi / yEd /
//   Cytoscape / NetworkX.
// --nodestore: also write "<output_graph>.nodes/" - a navigable node store on
//   disk (one file per SqlObject + one per shared Table/Column/Action/Rule,
//   each with its own adjacency) so an agent can read a few small files instead
//   of the whole graph_full.json. See NodeStoreExporter for the layout.
// Off by default - column lists come only from the SQL text (INSERT column
// list, UPDATE SET clauses, SELECT list) and have no data type, so this is an
// opt-in level of detail rather than a full schema.

using System.Text;
using System.Text.Json;
using TSqlParser;

var positional = args.Where(a => !a.StartsWith("--")).ToList();
var includeColumns = args.Contains("--columns");
var emitGraphify = args.Contains("--graphify");
var emitGraphml = args.Contains("--graphml");
var emitNodeStore = args.Contains("--nodestore");
var verifyAudit = args.Contains("--verify-audit");
var emitSqlite = args.Contains("--sqlite");

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

// "from-sql <database> <input.json> <file1.sql> [file2.sql ...|dir|glob]": builds
// input.json from local .sql files (no database connection). Each file should
// hold one CREATE [OR ALTER] PROC/FUNCTION/TRIGGER/VIEW/TABLE statement; the
// "schema.name" is detected from that statement (default schema "dbo").
if (positional.Count >= 1 && positional[0] == "from-sql")
{
    if (positional.Count < 4)
    {
        Console.Error.WriteLine("Usage: TSqlParser from-sql <database> <input.json> <file1.sql> [file2.sql ...|dir|glob]");
        return 1;
    }
    return SqlFileLoader.Run(positional[1], positional[2], positional.Skip(3).ToList());
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

// "corpus list" / "corpus refresh <id> [--server <server>] [--write]": manages the
// evaluation corpora declared in eval/corpora.json.
//
// "refresh" regenerates a corpus (module definitions + table DDL) and its column-level
// oracle from the live database and DIFFS them against the frozen copy in the repo.
// It writes nothing unless --write is passed: detecting that the frozen copy drifted
// away from the source database is the cheap, repeatable operation; overwriting it
// moves the gate numbers, so it has to be asked for. Exit code 2 means drift, so the
// check form works as a CI guard.
if (positional.Count >= 1 && positional[0] == "corpus")
{
    var repoRoot = CorpusManifest.FindRepoRoot(Directory.GetCurrentDirectory())
                   ?? CorpusManifest.FindRepoRoot(AppContext.BaseDirectory);
    if (repoRoot == null)
    {
        Console.Error.WriteLine($"No se encontró {CorpusManifest.RelPath} subiendo desde {Directory.GetCurrentDirectory()}");
        return 1;
    }

    var sub = positional.Count >= 2 ? positional[1] : "list";
    if (sub == "list")
        return CorpusRefresher.List(repoRoot);

    if (sub == "refresh")
    {
        if (positional.Count < 3)
        {
            Console.Error.WriteLine("Usage: TSqlParser corpus refresh <id> [--server <server>] [--write]");
            return 1;
        }
        var corpusServerIdx = Array.IndexOf(args, "--server");
        var corpusServer = corpusServerIdx >= 0 && corpusServerIdx + 1 < args.Length ? args[corpusServerIdx + 1] : null;
        return CorpusRefresher.Refresh(repoRoot, positional[2], corpusServer, args.Contains("--write"));
    }

    Console.Error.WriteLine($"Subcomando desconocido '{sub}'. Usage: TSqlParser corpus [list | refresh <id> [--server <server>] [--write]]");
    return 1;
}

// "mcp --store <graph_full.db>": speaks MCP (JSON-RPC 2.0 over stdio) so an agent can
// query the graph live - resolve_object (name -> canonical id) and impact (transitive
// blast radius) - instead of scanning graph_full.json. Read-only. See McpServer.
if (positional.Count >= 1 && positional[0] == "mcp")
{
    var mcpStoreIdx = Array.IndexOf(args, "--store");
    if (mcpStoreIdx < 0 || mcpStoreIdx + 1 >= args.Length)
    {
        Console.Error.WriteLine("Usage: TSqlParser mcp --store <graph_full.db>");
        return 1;
    }
    return new McpServer().Run(args[mcpStoreIdx + 1]);
}

// "blind-refs <corpusId> <salida.csv>": vuelca la LISTA de referencias (módulo, columna) que el
// referencia de eval/corpora.json ve y el grafo no reproduce (el conjunto laxo de
// ColumnRecallGateTests) - el gate solo imprime el agregado, esto imprime el detalle para poder
// clasificar cada ciega manualmente. <corpusId> se resuelve por CorpusManifest ("dnn", "wwidw",
// ...); la comparación es EXACTAMENTE la del gate (BlindRefs.Compute), así que las dos medidas
// no pueden divergir.
if (positional.Count >= 1 && positional[0] == "blind-refs")
{
    if (positional.Count < 3)
    {
        Console.Error.WriteLine("Usage: TSqlParser blind-refs <corpusId> <salida.csv>");
        return 1;
    }
    var blindRepoRoot = CorpusManifest.FindRepoRoot(Directory.GetCurrentDirectory())
                        ?? CorpusManifest.FindRepoRoot(AppContext.BaseDirectory);
    if (blindRepoRoot == null)
    {
        Console.Error.WriteLine($"No se encontró {CorpusManifest.RelPath} subiendo desde {Directory.GetCurrentDirectory()}");
        return 1;
    }
    var blindManifest = CorpusManifest.Load(blindRepoRoot);
    var blindCorpus = blindManifest.Find(positional[1]);
    if (blindCorpus == null)
    {
        Console.Error.WriteLine($"No hay corpus '{positional[1]}' en {CorpusManifest.RelPath}. Declarados: " +
            string.Join(", ", blindManifest.Corpora.Select(c => c.Id)));
        return 1;
    }
    if (blindCorpus.Catalog == null)
    {
        Console.Error.WriteLine($"El corpus '{blindCorpus.Id}' no declara catálogo de columna, no se puede calcular blind-refs.");
        return 1;
    }
    var blindResult = BlindRefs.Compute(blindCorpus.InputPath(blindRepoRoot), blindCorpus.CatalogPath(blindRepoRoot));
    BlindRefs.WriteCsv(blindResult, positional[2]);
    Console.WriteLine(BlindRefs.Summarize(blindCorpus.Id, blindResult, positional[2]));
    return 0;
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
    var (repResults, repTables) = InputAnalyzer.Analyze(positional[1]);
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

// "enrich-from-plans <graph.json> <output_graph.json> <plan1.xml> [plan2.xml ...]":
// merges SQL Server execution plan XML (ShowPlanXML / .sqlplan) into an existing
// static-analysis graph. Confirms static READS_FROM/WRITES_TO with runtime data
// (confidence=1.0, actual_rows) and discovers tables not visible statically
// (dynamic SQL resolved at runtime, view base tables, linked server objects).
if (positional.Count >= 1 && positional[0] == "enrich-from-plans")
{
    if (positional.Count < 4)
    {
        Console.Error.WriteLine("Usage: TSqlParser enrich-from-plans <graph.json> <output_graph.json> <plan1.xml> [plan2.xml ...]");
        return 1;
    }
    var enrichJsonOpts = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    var enrichGraphJson = File.ReadAllText(positional[1]);
    var enrichGraph = JsonSerializer.Deserialize<GraphPayload>(enrichGraphJson, enrichJsonOpts)!;

    var plans = new List<ExecutionPlanParser.ParsedPlan>();
    foreach (var planPath in positional.Skip(3))
    {
        if (!File.Exists(planPath))
        {
            Console.Error.WriteLine($"Plan file not found: {planPath}");
            continue;
        }
        var plan = ExecutionPlanParser.Parse(planPath);
        plans.Add(plan);
        Console.Error.Write(ExecutionPlanParser.Summarize(plan));
    }

    var enrichStats = PlanEnricher.Enrich(enrichGraph, plans);
    Utf8Io.WriteAllText(positional[2], JsonSerializer.Serialize(enrichGraph, enrichJsonOpts));
    Console.WriteLine($"Plans: {enrichStats.PlansProcessed}  Procs matched: {enrichStats.ProcsMatched}  " +
                      $"Confirmed: {enrichStats.RelationshipsConfirmed}  Discovered: {enrichStats.RelationshipsDiscovered} -> {positional[2]}");
    return 0;
}

// "capture-plans <database> <outputDir> [--server <server>] [--exec-file <path.sql>] [--wait-seconds N]":
// stands up an Extended Events session (query_post_execution_showplan +
// event_file target) over <database>, runs the workload (from --exec-file, or
// waits for the user to run it manually / --wait-seconds), stops the session,
// correlates ADHOC/PREPARED dynamic-SQL events with their parent PROC via
// nest_level, and writes one ShowPlanXML per procedure to <outputDir>/plans/
// - ready to feed straight into "enrich-from-plans". Always stops/drops the
// session and deletes the .xel files on the way out, even on failure:
// query_post_execution_showplan is an expensive event to leave running.
if (positional.Count >= 1 && positional[0] == "capture-plans")
{
    if (positional.Count < 3)
    {
        Console.Error.WriteLine("Usage: TSqlParser capture-plans <database> <outputDir> [--server <server>] [--exec-file <path.sql>] [--wait-seconds N]");
        return 1;
    }
    var serverArgIdx = Array.IndexOf(args, "--server");
    var server = serverArgIdx >= 0 && serverArgIdx + 1 < args.Length ? args[serverArgIdx + 1] : @".\SQLEXPRESS";
    var execFileArgIdx = Array.IndexOf(args, "--exec-file");
    var execFile = execFileArgIdx >= 0 && execFileArgIdx + 1 < args.Length ? args[execFileArgIdx + 1] : null;
    var waitArgIdx = Array.IndexOf(args, "--wait-seconds");
    var waitSeconds = waitArgIdx >= 0 && waitArgIdx + 1 < args.Length && int.TryParse(args[waitArgIdx + 1], out var ws) ? ws : 0;

    return XePlanCaptor.Run(server, positional[1], positional[2], execFile, waitSeconds);
}

// "plan-summary <plan.xml> [plan2.xml ...]": shows what tables each plan reads/writes.
// Useful for quick inspection of a plan file before integrating into a graph.
if (positional.Count >= 1 && positional[0] == "plan-summary")
{
    if (positional.Count < 2)
    {
        Console.Error.WriteLine("Usage: TSqlParser plan-summary <plan.xml> [plan2.xml ...]");
        return 1;
    }
    foreach (var planPath in positional.Skip(1))
    {
        if (!File.Exists(planPath))
        {
            Console.Error.WriteLine($"Plan file not found: {planPath}");
            continue;
        }
        Console.Write(ExecutionPlanParser.Summarize(ExecutionPlanParser.Parse(planPath)));
    }
    return 0;
}

// "update-nodestore <input.json> <store_dir.nodes> [--columns]": incremental
// refresh of a node store previously written with --nodestore. Re-analyzes the
// whole input (cheap) but only rewrites objects/** and shared/** files whose
// content actually changed since the existing manifest.json, GCs files for
// removed objects/orphaned shared nodes, and always refreshes model.json,
// manifest.json and index.json. If <store_dir.nodes> doesn't exist yet, behaves
// like a full --nodestore write.
if (positional.Count >= 1 && positional[0] == "update-nodestore")
{
    if (positional.Count < 3)
    {
        Console.Error.WriteLine("Usage: TSqlParser update-nodestore <input.json> <store_dir.nodes> [--columns]");
        return 1;
    }
    var (updResults, updTableSchemas) = InputAnalyzer.Analyze(positional[1]);
    var updGraph = GraphExporter.Build(updResults, includeColumns, updTableSchemas);
    var updDb = updResults.Select(o => o.ObjectName.Split("::", 2)).FirstOrDefault(p => p.Length == 2)?[0] ?? "";
    var updJsonOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    var updStats = NodeStoreExporter.Update(updGraph, positional[2], updDb, updJsonOptions);
    Console.WriteLine($"Updated: {updStats.ObjectsWritten} objects ({updStats.ObjectsUnchanged} unchanged, {updStats.ObjectsRemoved} removed), shared: {updStats.SharedWritten} ({updStats.SharedUnchanged} unchanged, {updStats.SharedRemoved} removed) -> {positional[2]}");
    if (verifyAudit) return AuditVerifier.Verify(positional[2]);
    return 0;
}

// "diff-change-map <store_before.nodes> <store_after.nodes> <output.json> [--fail-on-new-impact]":
// diffs two already-generated node stores into a change_map_diff.json (which objects
// changed, what impact they gained/lost, whom they now reach). Reads ONLY manifest.json
// (content_hash) + change_map.json from each store - no SQL re-analysis. With
// --fail-on-new-impact, exits 2 when the diff surfaces new impact (an optional CI gate).
// See ChangeMapDiff / docs/task-change-map-diff.md.
if (positional.Count >= 1 && positional[0] == "diff-change-map")
{
    if (positional.Count < 4)
    {
        Console.Error.WriteLine("Usage: TSqlParser diff-change-map <store_before.nodes> <store_after.nodes> <output.json> [--fail-on-new-impact]");
        return 1;
    }
    var diffJsonOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    var failOnNewImpact = args.Contains("--fail-on-new-impact");
    return ChangeMapDiff.Run(positional[1], positional[2], positional[3], failOnNewImpact, diffJsonOptions);
}

// "bench-make <store_dir.nodes> <bench_dir>" / "bench-grade <bench_dir> <answers_dir>":
// store-agnostic agent benchmark. bench-make derives six navigation cases (prompts +
// expected answers) from any nodestore's own precomputed artifacts; bench-grade scores
// one model's answers directory (answers/<model>/C*.json [+ run.json metadata]) and
// writes a per-model scorecard under <bench_dir>/results/. See AgentBench /
// eval/agent-bench/README.md.
if (positional.Count >= 1 && positional[0] is "bench-make" or "bench-grade")
{
    if (positional.Count < 3)
    {
        Console.Error.WriteLine($"Usage: TSqlParser {positional[0]} {(positional[0] == "bench-make" ? "<store_dir.nodes> <bench_dir>" : "<bench_dir> <answers_dir>")}");
        return 1;
    }
    var seedIdx = Array.IndexOf(args, "--seed");
    var benchSeed = seedIdx >= 0 && seedIdx + 1 < args.Length && int.TryParse(args[seedIdx + 1], out var sv) ? sv : 0;
    return positional[0] == "bench-make"
        ? AgentBench.Make(positional[1], positional[2], benchSeed)
        : AgentBench.Grade(positional[1], positional[2]);
}

if (positional.Count < 2)
{
    Console.Error.WriteLine("Usage: TSqlParser <input.json> <output_graph.json> [output_workflows.json] [--columns] [--graphify] [--graphml] [--nodestore] [--sqlite]");
    Console.Error.WriteLine("       TSqlParser report <input.json> [nombre-objeto]");
    Console.Error.WriteLine("       TSqlParser from-sql <database> <input.json> <file1.sql> [file2.sql ...|dir|glob]");
    Console.Error.WriteLine("       TSqlParser extract <database> <input.json> [--server <server>] [--tables] [--object schema.name]... [--like pattern]");
    Console.Error.WriteLine("       TSqlParser validate <graph.json> [--server <server>]");
    Console.Error.WriteLine("       TSqlParser extract-tables <graph.json> <input.json> [--server <server>]");
    Console.Error.WriteLine("       TSqlParser update-nodestore <input.json> <store_dir.nodes> [--columns]");
    Console.Error.WriteLine("       TSqlParser diff-change-map <store_before.nodes> <store_after.nodes> <output.json> [--fail-on-new-impact]");
    Console.Error.WriteLine("       TSqlParser bench-make <store_dir.nodes> <bench_dir>  |  bench-grade <bench_dir> <answers_dir>");
    Console.Error.WriteLine("       TSqlParser blind-refs <corpusId> <salida.csv>");
    Console.Error.WriteLine("       TSqlParser mcp --store <graph_full.db>");
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

var (results, tableSchemas) = InputAnalyzer.Analyze(inputPath);

if (workflowsOutputPath != null)
    Utf8Io.WriteAllText(workflowsOutputPath, JsonSerializer.Serialize(results, jsonOptions));

var graph = GraphExporter.Build(results, includeColumns, tableSchemas);
Utf8Io.WriteAllText(graphOutputPath, JsonSerializer.Serialize(graph, jsonOptions));

// Cada formato opcional es un IGraphSink registrado en GraphSinks.Default; el flag que
// lo activa y la extensión de salida los declara él. Un formato nuevo no toca este fichero.
var dbName = results.Select(o => o.ObjectName.Split("::", 2)).FirstOrDefault(p => p.Length == 2)?[0] ?? "";
var exportContext = new ExportContext
{
    GraphOutputPath = graphOutputPath,
    Database = dbName,
    // --project=<name> identifies the analysis project; defaults to the database name.
    Project = args.FirstOrDefault(a => a.StartsWith("--project="))?["--project=".Length..] ?? dbName,
    JsonOptions = jsonOptions,
};

string? nodeStorePath = null;
foreach (var sink in GraphSinks.Default.Where(s => args.Contains(s.Flag)))
{
    var sinkResult = sink.Write(graph, exportContext);
    Console.WriteLine(sinkResult.Summary);
    if (sink.Flag == "--nodestore")
        nodeStorePath = sinkResult.OutputPath;
}

// --verify-audit valida los invariantes del audit_report.json que escribe el nodestore;
// sin --nodestore no hay nada que verificar.
if (verifyAudit && nodeStorePath != null)
    return AuditVerifier.Verify(nodeStorePath);

var ok = results.Count(r => r.Error == null);
Console.WriteLine($"Analyzed {results.Count} objects ({ok} ok, {results.Count - ok} parse errors)");
if (tableSchemas.Count > 0)
{
    var tableOk = tableSchemas.Count(t => t.Error == null);
    Console.WriteLine($"Analyzed {tableSchemas.Count} table schemas ({tableOk} ok, {tableSchemas.Count - tableOk} errors)");
}
Console.WriteLine($"Graph: {graph.Nodes.Count} nodes, {graph.Relationships.Count} relationships -> {graphOutputPath}");
return 0;
