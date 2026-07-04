using System.Text.Json;
using System.Text.RegularExpressions;

namespace TSqlParser;

/// <summary>One bad-practice / risk finding over the graph. Severities: crit | high | med | low | info.
/// Categories: Seguridad, Robustez, Rendimiento, Mantenibilidad, Integridad, Diseño.</summary>
public record RiskFinding(string Sev, string Cat, string Rule, string Component, string Detail);

/// <summary>
/// Bad-practice / risk rule engine over a <see cref="GraphPayload"/> - the C# single
/// source of truth for the rules that dashboard/src/risks.js applies in the browser
/// (rules based on T-SQL anti-pattern catalogs: Simple Talk "40 problems", SQLBlog
/// best-practices checklist, Brent Ozar). This is a faithful port of risks.js plus
/// the subset of shape.js it consumes; the ground truth that pins the parity is
/// eval/bad-practices/expected-findings.json (see BadPracticesGateTests).
/// Works both on an in-process graph (native property values) and on a graph
/// deserialized from graph_full.json (JsonElement property values).
/// </summary>
public static class RiskAnalyzer
{
    private static readonly string[] SevOrder = { "crit", "high", "med", "low", "info" };

    public static List<RiskFinding> Analyze(GraphPayload graph)
    {
        var byId = graph.Nodes.ToDictionary(n => n.Id, n => n);
        var outEdges = new Dictionary<string, List<GraphRel>>();
        var inEdges = new Dictionary<string, List<GraphRel>>();
        foreach (var r in graph.Relationships)
        {
            (outEdges.TryGetValue(r.StartNodeId, out var o) ? o : outEdges[r.StartNodeId] = new()).Add(r);
            (inEdges.TryGetValue(r.EndNodeId, out var i) ? i : inEdges[r.EndNodeId] = new()).Add(r);
        }
        IEnumerable<GraphRel> OutOf(string id, string type) =>
            outEdges.TryGetValue(id, out var l) ? l.Where(r => r.Type == type) : Enumerable.Empty<GraphRel>();
        IEnumerable<GraphRel> IncOf(string id, string type) =>
            inEdges.TryGetValue(id, out var l) ? l.Where(r => r.Type == type) : Enumerable.Empty<GraphRel>();

        // stepId -> objId (owner), via HAS_STEP - used to attribute a table's
        // readers/writers to the object whose step touches it.
        var stepOwner = new Dictionary<string, string>();
        foreach (var r in graph.Relationships)
            if (r.Type == "HAS_STEP")
                stepOwner[r.EndNodeId] = r.StartNodeId;

        string NameOf(string id) => byId.TryGetValue(id, out var n)
            ? FirstNonEmpty(Str(n.Properties, "full_name"), Str(n.Properties, "name"), id)
            : id;

        var findings = new List<RiskFinding>();
        void Add(string sev, string cat, string rule, string component, string detail) =>
            findings.Add(new RiskFinding(sev, cat, rule, component, detail));

        // ── OBJETOS ───────────────────────────────────────────────
        foreach (var n in graph.Nodes.Where(n => n.Labels.Contains("SqlObject")))
        {
            var p = n.Properties;
            var name = FirstNonEmpty(Str(p, "full_name"), Str(p, "name"), n.Id);
            var complexity = Int(p, "cyclomatic_complexity");
            if (complexity == 0) complexity = 1;
            var hasTx = Flag(p, "has_transaction");
            var hasErr = Flag(p, "has_error_handling");
            var hasCursor = Flag(p, "has_cursor");
            var dyn = Int(p, "dynamic_sql_calls");
            var parseError = Str(p, "parse_error");

            var steps = OutOf(n.Id, "HAS_STEP")
                .Select(r => byId[r.EndNodeId])
                .OrderBy(s => Int(s.Properties, "order"))
                .ToList();
            var stepIds = steps.Select(s => s.Id).ToHashSet();

            // reads/writes at object level; writes de-duplicated per "action table",
            // writeTally counting raw WRITES_TO edges per table (like shape.js).
            var writes = new List<(string Op, string Table)>();
            var seenWrites = new HashSet<string>();
            var writeTally = new Dictionary<string, int>();
            foreach (var s in steps)
            {
                var action = Str(s.Properties, "action");
                foreach (var r in OutOf(s.Id, "WRITES_TO"))
                {
                    var t = Str(byId[r.EndNodeId].Properties, "name");
                    if (seenWrites.Add($"{action} {t}"))
                        writes.Add((action, t));
                    writeTally[t] = writeTally.GetValueOrDefault(t) + 1;
                }
            }

            var vars = OutOf(n.Id, "DECLARES").Select(r => byId[r.EndNodeId]).Select(v => (
                Name: Str(v.Properties, "name"),
                UsedBy: IncOf(v.Id, "USES_VARIABLE").Count(r2 => stepIds.Contains(r2.StartNodeId)),
                BuildsSql: IncOf(v.Id, "BUILDS_SQL_FROM").Count(),
                AssignedFrom: OutOf(v.Id, "ASSIGNED_FROM").Select(r2 =>
                {
                    var c = byId[r2.EndNodeId].Properties;
                    return $"{Str(c, "table")}({Str(c, "name")})";
                }).ToList()
            )).ToList();

            var callsIn = IncOf(n.Id, "CALLS").Select(r => NameOf(r.StartNodeId)).Distinct().ToList();

            // ── Seguridad ────────────────────────────────────────────
            var tainted = vars.Where(v => v.BuildsSql > 0 && v.AssignedFrom.Count > 0).ToList();
            if (tainted.Count > 0)
                Add("crit", "Seguridad", "Inyección SQL", name,
                    $"SQL dinámico construido desde datos de tabla: {string.Join("; ", tainted.Select(v => $"{v.Name} ← {string.Join(", ", v.AssignedFrom)}"))}");
            else if (dyn > 0)
            {
                var dynVars = vars.Where(v => v.BuildsSql > 0).Select(v => v.Name).ToList();
                Add("high", "Seguridad", "SQL dinámico", name,
                    $"Ejecuta {dyn} sentencia(s) de SQL dinámico{(dynVars.Count > 0 ? $" (desde {string.Join(", ", dynVars)})" : "")}: revisar parametrización/permisos.");
            }

            // ── Robustez (transacciones / errores) ───────────────────
            if (hasCursor && hasTx && !hasErr)
                Add("high", "Robustez", "Cursor en transacción sin TRY/CATCH", name,
                    "Cursor dentro de transacción sin manejo de errores: alto riesgo de transacción/recurso huérfano.");
            else if (hasTx && !hasErr)
                Add("high", "Robustez", "Transacción sin TRY/CATCH", name,
                    "Abre transacción sin manejo de errores: riesgo de transacción huérfana / bloqueo si falla.");

            if (writes.Count > 0 && !hasTx && !hasErr)
                Add("med", "Robustez", "Escritura sin protección", name,
                    $"Modifica datos ({string.Join(", ", writes.Select(w => w.Table).Distinct().Take(3))}) sin transacción ni manejo de errores.");

            // ── Rendimiento ──────────────────────────────────────────
            if (hasCursor)
                Add("med", "Rendimiento", "Uso de cursor", name,
                    "Cursor explícito: revisar si puede resolverse con operaciones de conjunto.");

            if (Regex.IsMatch(Leaf(name), "^sp_", RegexOptions.IgnoreCase))
                Add("med", "Rendimiento", "Prefijo sp_", name,
                    "El prefijo sp_ hace que SQL Server busque primero en master (penalización) y puede colisionar con procedimientos de sistema.");

            foreach (var (table, count) in writeTally)
                if (count >= 3)
                    Add("med", "Rendimiento", "Escrituras repetidas a la misma tabla", name,
                        $"Escribe {count} veces en {table}: posible candidato a consolidar en una sola operación de conjunto.");

            if (steps.Any(s => Flag(s.Properties, "select_star")))
                Add("low", "Rendimiento", "SELECT *", name,
                    "Usa SELECT *: trae columnas de más, rompe ante cambios de esquema e impide cubrir consultas con índices. Listar columnas explícitas.");

            // ── Integridad (operaciones destructivas) ────────────────
            var noWhere = steps.Where(s =>
            {
                var action = Str(s.Properties, "action");
                return (action == "UPDATE" || action == "DELETE") && !OutOf(s.Id, "FILTERS_ON").Any();
            }).ToList();
            if (noWhere.Count > 0)
                Add("high", "Integridad", "UPDATE/DELETE sin WHERE", name,
                    $"Modifica/borra sin filtro: {string.Join("; ", noWhere.Select(s => $"{Str(s.Properties, "action")} {StepTarget(s)}").Distinct())}. Afecta a TODAS las filas de la tabla.");

            var truncates = writes.Where(w => w.Op == "TRUNCATE").ToList();
            if (truncates.Count > 0)
                Add("med", "Integridad", "TRUNCATE de tabla", name,
                    $"TRUNCATE TABLE {string.Join(", ", truncates.Select(w => w.Table).Distinct())}: borrado masivo sin log por fila, reinicia IDENTITY y no respeta WHERE/triggers.");

            // ── Mantenibilidad ───────────────────────────────────────
            if (complexity >= 10)
                Add("med", "Mantenibilidad", "Complejidad alta", name,
                    $"Complejidad ciclomática {complexity} (difícil de mantener/probar).");
            else if (complexity >= 6)
                Add("low", "Mantenibilidad", "Complejidad moderada", name, $"Complejidad ciclomática {complexity}.");

            // Nesting depth = longest condition_path across the object's steps -
            // equivalent to risks.js treeDepth over the flow tree it builds from them.
            var depth = steps.Count > 0 ? steps.Max(s => PathLen(s.Properties, "condition_path")) : 0;
            if (depth >= 4)
                Add("med", "Mantenibilidad", "Anidación profunda", name,
                    $"Anidación de control de {depth} niveles: lógica difícil de seguir (considerar guardas/extraer procedimientos).");
            else if (depth == 3)
                Add("low", "Mantenibilidad", "Anidación profunda", name, $"Anidación de control de {depth} niveles.");

            var unused = vars.Where(v => v.UsedBy == 0 && v.BuildsSql == 0 && v.AssignedFrom.Count == 0).ToList();
            if (unused.Count > 0)
                Add("low", "Mantenibilidad", "Variable sin uso", name,
                    $"Variables declaradas y nunca usadas: {string.Join(", ", unused.Select(v => v.Name))}.");

            if (callsIn.Count == 0 && writes.Count == 0 && steps.Count > 0 &&
                Regex.IsMatch(name, @"\bufn|\bfn|function", RegexOptions.IgnoreCase))
                Add("low", "Mantenibilidad", "Posible código muerto", name,
                    "Función sin llamadores detectados (puede usarse en vistas/columnas calculadas no analizadas, o estar obsoleta).");

            // ── Diseño ───────────────────────────────────────────────
            var distinctWrites = writes.Select(w => w.Table).Distinct().ToList();
            if (distinctWrites.Count >= 5)
                Add("med", "Diseño", "Objeto hace demasiado", name,
                    $"Escribe en {distinctWrites.Count} tablas distintas ({string.Join(", ", distinctWrites.Take(4))}…): candidato a dividir.");

            if (parseError.Length > 0)
                Add("info", "Mantenibilidad", "Error de parseo", name, parseError);
        }

        // ── Riesgos a nivel de TABLA ───────────────────────────────
        foreach (var n in graph.Nodes.Where(n => n.Labels.Contains("Table")))
        {
            var name = Str(n.Properties, "name");
            var columns = OutOf(n.Id, "HAS_COLUMN").Select(r => byId[r.EndNodeId].Properties).ToList();
            // Like shape.js: a column is nullable unless is_nullable is explicitly false,
            // so a table materialized from references (no DDL) counts as fully nullable.
            var colCount = columns.Count;
            var hasPk = columns.Any(c => Flag(c, "is_primary_key"));
            var allNullable = colCount > 0 && columns.All(c => NotFalse(c, "is_nullable"));

            var writers = new HashSet<string>();
            foreach (var r in IncOf(n.Id, "WRITES_TO"))
            {
                var owner = stepOwner.TryGetValue(r.StartNodeId, out var oid) ? NameOf(oid) : r.StartNodeId;
                var op = byId.TryGetValue(r.StartNodeId, out var s) ? Str(s.Properties, "action") : "";
                writers.Add($"{op} {owner}");
            }
            var readers = new HashSet<string>();
            foreach (var r in IncOf(n.Id, "READS_FROM"))
                readers.Add(stepOwner.TryGetValue(r.StartNodeId, out var oid) ? NameOf(oid) : r.StartNodeId);

            if (colCount > 0 && !hasPk)
                Add("med", "Integridad", "Tabla sin clave primaria", name,
                    $"{colCount} columnas y ninguna PK: riesgo de duplicados y replicación/merge problemáticos.");

            if (writers.Count >= 4)
                Add("med", "Diseño", "Acoplamiento alto (tabla)", name,
                    $"Escrita por {writers.Count} objetos: cualquier cambio de esquema impacta a muchos.");

            if (writers.Count > 0 && readers.Count == 0 && colCount > 0)
                Add("low", "Diseño", "Tabla escrita pero nunca leída", name,
                    "Recibe escrituras pero ningún objeto analizado la lee (staging, auditoría o dato muerto).");

            if (allNullable)
                Add("low", "Integridad", "Tabla totalmente anulable", name,
                    $"Las {colCount} columnas admiten NULL: sin NOT NULL ni PK que garantice una fila identificable y con datos mínimos.");

            if (colCount >= 12)
                Add("low", "Diseño", "Tabla ancha", name,
                    $"{colCount} columnas: posible tabla \"Dios\" (mezcla responsabilidades), candidata a normalizar/dividir.");
        }

        return findings
            .OrderBy(f => Array.IndexOf(SevOrder, f.Sev))
            .ThenBy(f => f.Cat, StringComparer.Ordinal)
            .ThenBy(f => f.Component, StringComparer.Ordinal)
            .ToList();

        string StepTarget(GraphNode s)
        {
            var targets = OutOf(s.Id, "TARGETS").FirstOrDefault();
            return targets != null ? NameOf(targets.EndNodeId) : Str(s.Properties, "target_name");
        }
    }

    private static string Leaf(string name) => name.Split('.')[^1];

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? "";

    // Property helpers tolerating both in-process values (bool/int/string/lists)
    // and JsonElement values from a deserialized graph_full.json.
    private static string Str(Dictionary<string, object> p, string key) =>
        p.TryGetValue(key, out var v) ? v switch
        {
            null => "",
            string s => s,
            JsonElement e when e.ValueKind == JsonValueKind.String => e.GetString() ?? "",
            JsonElement => "",
            _ => v.ToString() ?? "",
        } : "";

    private static bool Flag(Dictionary<string, object> p, string key) =>
        p.TryGetValue(key, out var v) && v switch
        {
            bool b => b,
            JsonElement e => e.ValueKind == JsonValueKind.True,
            _ => false,
        };

    /// <summary>true unless the property exists and is explicitly false (JS: v !== false).</summary>
    private static bool NotFalse(Dictionary<string, object> p, string key) =>
        !p.TryGetValue(key, out var v) || v switch
        {
            bool b => b,
            JsonElement e => e.ValueKind != JsonValueKind.False,
            _ => true,
        };

    private static int Int(Dictionary<string, object> p, string key) =>
        p.TryGetValue(key, out var v) ? v switch
        {
            int i => i,
            long l => (int)l,
            JsonElement e when e.ValueKind == JsonValueKind.Number => e.GetInt32(),
            _ => 0,
        } : 0;

    private static int PathLen(Dictionary<string, object> p, string key) =>
        p.TryGetValue(key, out var v) ? v switch
        {
            JsonElement e when e.ValueKind == JsonValueKind.Array => e.GetArrayLength(),
            System.Collections.IEnumerable en and not string => en.Cast<object>().Count(),
            _ => 0,
        } : 0;
}
