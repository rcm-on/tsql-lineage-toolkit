using Microsoft.Data.Sqlite;

namespace Parser.Mcp;

/// <summary>Signals a rejected/failed tool call (bad arguments, unknown id) - never an
/// uncaught exception, always surfaced by the transport as a normal tools/call result
/// with isError:true.</summary>
public sealed class McpToolException : Exception
{
    public McpToolException(string message) : base(message) { }
}

/// <summary>
/// Pure query handlers behind the two MCP tools: (open read-only SqliteConnection,
/// arguments) -&gt; plain Dictionary response, independent of stdio/JSON-RPC so they're
/// testable without a process. Queries the graph_full.db schema written by
/// SqliteExporter: nodes(id,label,name,...), edges(src,dst,type,action_type,props).
/// Every response stays deliberately thin (ids/names only, no props) to fit an
/// agent's context budget - see ResponseBudgetBytes.
/// </summary>
public static class McpTools
{
    /// <summary>Hard target for a serialized response at default limits (see McpTests
    /// BudgetGateTests) - this is a design gate, not a soft guideline.</summary>
    public const int ResponseBudgetBytes = 2048;

    private static readonly IReadOnlyList<string> ImpactEdgeTypes = StoreSchema.ImpactEdgeTypes;
    private static readonly string ImpactEdgeTypesLabel = string.Join("/", ImpactEdgeTypes);
    private static readonly string AddressableLabelsCsv =
        string.Join(",", StoreSchema.AddressableLabels.Select(l => $"'{l}'"));

    // Defensive ceiling on BFS size so a pathological depth/limit combination on a huge
    // graph can't turn one tool call into an unbounded scan; independent of `limit`.
    private const int SafetyCap = 5000;
    private const int SqliteInBatchSize = 500;

    public static Dictionary<string, object?> ResolveObject(SqliteConnection conn, string name, int limit)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new McpToolException("resolve_object: 'name' no puede estar vacío.");
        if (limit <= 0) limit = 10;
        limit = Math.Min(limit, 200);

        var needle = name.Trim();
        var needleLower = needle.ToLowerInvariant();
        var candidates = new List<(string Id, string Label, string Name, int Score)>();

        using (var cmd = conn.CreateCommand())
        {
            // Only the entities an agent could plausibly mean by a loose name - Step/
            // Action/Variable/Rule/etc. are graph-internal plumbing, not addressable objects.
            cmd.CommandText =
                "SELECT id, label, name FROM nodes " +
                $"WHERE label IN ({AddressableLabelsCsv}) " +
                "  AND (lower(name) LIKE '%' || $n || '%' ESCAPE '\\' " +
                "       OR lower(id) LIKE '%' || $n || '%' ESCAPE '\\')";
            cmd.Parameters.AddWithValue("$n", EscapeLike(needleLower));
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetString(0);
                var label = reader.IsDBNull(1) ? "" : reader.GetString(1);
                var nodeName = reader.IsDBNull(2) ? "" : reader.GetString(2);
                var score = MatchScore(id, nodeName, needleLower);
                if (score > 0)
                    candidates.Add((id, label, nodeName, score));
            }
        }

        var ordered = candidates
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.Name.Length)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var total = ordered.Count;
        var exactCount = ordered.Count(c => c.Score == 3);
        var page = ordered.Take(limit).ToList();

        var result = new Dictionary<string, object?>
        {
            ["matches"] = page.Select(c => new Dictionary<string, object?>
            {
                ["id"] = c.Id,
                ["label"] = c.Label,
                ["name"] = c.Name,
            }).ToList(),
            ["total"] = total,
        };
        if (total > limit) result["truncated"] = true;
        if (exactCount == 1) result["exact"] = true;
        return result;
    }

    public static Dictionary<string, object?> Impact(SqliteConnection conn, string id, string direction, int depth, int limit)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new McpToolException("impact: 'id' no puede estar vacío.");
        direction = string.IsNullOrWhiteSpace(direction) ? "downstream" : direction.Trim().ToLowerInvariant();
        if (direction is not ("downstream" or "upstream"))
            throw new McpToolException($"impact: direction debe ser 'downstream' o 'upstream', no '{direction}'.");
        if (depth < 1 || depth > 5)
            throw new McpToolException("impact: depth debe estar entre 1 y 5.");
        if (limit <= 0) limit = 50;
        limit = Math.Min(limit, 500);

        if (!NodeExists(conn, id))
            throw new McpToolException($"impact: no existe ningún nodo con id '{id}'.");

        // Edges point actor -> resource-acted-upon (caller->callee, reader->table,
        // writer->table, derived-column->source-column). "downstream" (what breaks if
        // `id` changes) walks edges backwards (dst==frontier, collect src); "upstream"
        // (what `id` depends on) walks them forwards (src==frontier, collect dst).
        var matchColumn = direction == "downstream" ? "dst" : "src";
        var otherColumn = direction == "downstream" ? "src" : "dst";

        var visited = new HashSet<string>(StringComparer.Ordinal) { id };
        var frontier = new List<string> { id };
        var affected = new List<(string Id, int Hops)>();

        for (var hop = 1; hop <= depth && frontier.Count > 0 && affected.Count < SafetyCap; hop++)
        {
            var next = new List<string>();
            foreach (var otherId in QueryNeighbors(conn, frontier, matchColumn, otherColumn))
            {
                if (!visited.Add(otherId)) continue;
                next.Add(otherId);
                affected.Add((otherId, hop));
            }
            frontier = next;
        }

        var total = affected.Count;
        var page = affected.Take(limit).ToList();
        var info = LookupNodes(conn, page.Select(p => p.Id));

        var items = page.Select(p =>
        {
            var (label, nodeName) = info.TryGetValue(p.Id, out var v) ? v : ("", p.Id);
            return new Dictionary<string, object?>
            {
                ["id"] = p.Id,
                ["name"] = nodeName,
                ["label"] = label,
                ["hops"] = p.Hops,
            };
        }).ToList();

        var result = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["direction"] = direction,
            ["depth"] = depth,
            ["affected"] = items,
            ["total"] = total,
        };
        if (total > limit) result["truncated"] = true;

        // An empty "affected" reads as a fact ("nothing depends on this") unless it
        // carries its own explanation - distinguish "no such edges at all" from "the
        // only ones found loop back to the node itself" (self-recursion; depth can't
        // fix that), and check whether the other direction would have answered instead.
        if (total == 0)
        {
            var hasRawEdge = HasAnyEdge(conn, id, matchColumn);
            result["reason"] = hasRawEdge
                ? "las únicas aristas encontradas vuelven al propio nodo (auto-referencia); depth no lo cambia."
                : direction == "downstream"
                    ? $"sin aristas {ImpactEdgeTypesLabel} entrantes a este nodo."
                    : $"sin aristas {ImpactEdgeTypesLabel} salientes de este nodo.";

            var oppositeDirection = direction == "downstream" ? "upstream" : "downstream";
            var oppositeMatch = otherColumn;
            var oppositeOther = matchColumn;
            var oppositeCount = QueryNeighbors(conn, [id], oppositeMatch, oppositeOther).Count(x => x != id);
            if (oppositeCount > 0)
                result["hint"] = $"0 en {direction}, pero {oppositeDirection} tiene {oppositeCount} objeto(s) a 1 salto - prueba direction={oppositeDirection}.";
        }

        return result;
    }

    /// <summary>
    /// De dónde viene el VALOR de una columna: DERIVES_FROM hacia delante (src = columna
    /// alcanzada, dst = fuente de la que se calcula). Ordenado de más profundo a más
    /// cercano, que es el orden de remediación: se arregla primero el origen.
    /// </summary>
    public static Dictionary<string, object?> ColumnProvenance(SqliteConnection conn, string id, int depth, int limit)
        => ColumnChain(conn, id, depth, limit, haciaFuentes: true);

    /// <summary>
    /// Qué se rompe si cambio una columna. Dos respuestas distintas que no hay que
    /// mezclar: qué OBJETOS la referencian (con su confianza) y qué COLUMNAS se calculan
    /// a partir de ella. Más el descargo de los objetos cuyo SQL dinámico no resolvió:
    /// podrían tocarla y el motor no puede probarlo ni descartarlo.
    /// </summary>
    public static Dictionary<string, object?> ColumnImpact(SqliteConnection conn, string id, int depth, int limit)
    {
        ValidarColumna(conn, id, "column_impact", out var db);
        if (depth < 1 || depth > 5) throw new McpToolException("column_impact: depth debe estar entre 1 y 5.");
        if (limit <= 0) limit = 15;
        limit = Math.Min(limit, 200);

        // Confianza por objeto: una referencia directa basta, por muchas aristas débiles
        // que tenga el mismo objeto a la misma columna. Nunca se promedian ni se
        // multiplican: los errores están correlacionados, se toma el mejor caso.
        var porObjeto = new Dictionary<string, (bool Directa, bool ViaVista, bool Estrella)>(StringComparer.Ordinal);
        using (var cmd = conn.CreateCommand())
        {
            var tipos = string.Join(",", StoreSchema.ColumnRefEdgeTypes.Select(t => $"'{t}'"));
            cmd.CommandText =
                $"SELECT src, json_extract(props,'$.resolution'), json_extract(props,'$.via_view') " +
                $"FROM edges WHERE dst = $id AND type IN ({tipos})";
            cmd.Parameters.AddWithValue("$id", id);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var obj = StoreSchema.RollUpStep(reader.GetString(0));
                var res = reader.IsDBNull(1) ? "" : reader.GetString(1);
                var viaVista = !reader.IsDBNull(2);
                porObjeto.TryGetValue(obj, out var v);
                porObjeto[obj] = (v.Directa || res == StoreSchema.Resolution.Direct,
                                  v.ViaVista || viaVista || res == StoreSchema.Resolution.ViaView,
                                  v.Estrella || res == StoreSchema.Resolution.StarExpanded);
            }
        }

        var info = LookupNodes(conn, porObjeto.Keys);
        var objetos = porObjeto
            .Where(p => info.TryGetValue(p.Key, out var i) && i.Label == "SqlObject")
            .OrderByDescending(p => p.Value.Directa)
            .ThenBy(p => info[p.Key].Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var pagina = objetos.Take(limit).Select(p =>
        {
            var d = new Dictionary<string, object?>
            {
                ["id"] = p.Key,
                ["name"] = info[p.Key].Name,
                ["confianza"] = p.Value.Directa ? "seguro" : "probable",
            };
            if (!p.Value.Directa)
                d["motivo"] = p.Value.ViaVista ? "via vista" : "de SELECT *";
            return d;
        }).ToList();

        var columnas = RecorrerDerives(conn, id, depth, haciaFuentes: false);
        var columnasPagina = columnas.Take(limit)
            .Select(c => new Dictionary<string, object?> { ["id"] = c.Id, ["hops"] = c.Hops })
            .ToList();

        var result = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["objects"] = pagina,
            ["objects_total"] = objetos.Count,
            ["columns"] = columnasPagina,
            ["columns_total"] = columnas.Count,
        };
        if (objetos.Count > limit || columnas.Count > limit) result["truncated"] = true;

        // Descargo incondicional, no "no hay impacto": si supiéramos qué tocan, el SQL
        // habría resuelto. Se declara aunque haya resultados.
        var ciegos = ContarDinamicoSinResolver(conn, db);
        if (ciegos > 0)
            result["desconocido"] = new Dictionary<string, object?>
            {
                ["objetos"] = ciegos,
                ["motivo"] = "objetos de la misma base con SQL dinámico sin resolver: podrían referenciar esta columna y el motor no tiene arista que lo pruebe ni lo descarte.",
            };

        if (objetos.Count == 0 && columnas.Count == 0)
            result["reason"] = "ningún objeto la referencia y ninguna columna se calcula a partir de ella en este grafo.";

        return result;
    }

    /// <summary>
    /// Vistazo del store: metadatos de generación, tamaño por label/type, y cuánto SQL
    /// dinámico quedó sin resolver. La razón de ser es el aviso de antigüedad: un store
    /// generado hace semanas responde con normalidad a cualquier consulta y miente en
    /// silencio sobre el estado actual de la base.
    /// </summary>
    public static Dictionary<string, object?> StoreInfo(SqliteConnection conn)
    {
        Dictionary<string, string> meta;
        List<(string Name, int Count)> nodosPorLabel, aristasPorType;
        int ciegos;
        try
        {
            meta = ReadMeta(conn);
            nodosPorLabel = GroupCount(conn, "nodes", "label");
            aristasPorType = GroupCount(conn, "edges", "type");
            ciegos = ScalarInt(conn,
                "SELECT COUNT(*) FROM nodes WHERE label = 'SqlObject' AND unresolved_dynamic_sql_steps > 0");
        }
        catch (SqliteException ex)
        {
            // Un store sin la tabla meta o sin las columnas esperadas no es "0 en silencio":
            // es un esquema distinto o incompleto, y hay que decirlo, no fingir que no hay datos.
            throw new McpToolException($"store_info: el esquema del store no es el esperado ({ex.Message}).");
        }

        var truncated = nodosPorLabel.Count > 8 || aristasPorType.Count > 8;

        var result = new Dictionary<string, object?>
        {
            [StoreSchema.MetaKeys.Database] = meta.GetValueOrDefault(StoreSchema.MetaKeys.Database),
            [StoreSchema.MetaKeys.Project] = meta.GetValueOrDefault(StoreSchema.MetaKeys.Project),
            [StoreSchema.MetaKeys.GeneratedAt] = meta.GetValueOrDefault(StoreSchema.MetaKeys.GeneratedAt),
            [StoreSchema.MetaKeys.Format] = meta.GetValueOrDefault(StoreSchema.MetaKeys.Format),
            [StoreSchema.MetaKeys.NodeCount] = ParseIntOr(meta.GetValueOrDefault(StoreSchema.MetaKeys.NodeCount)),
            [StoreSchema.MetaKeys.EdgeCount] = ParseIntOr(meta.GetValueOrDefault(StoreSchema.MetaKeys.EdgeCount)),
            ["nodes_by_label"] = nodosPorLabel.Take(8)
                .Select(x => new Dictionary<string, object?> { ["label"] = x.Name, ["count"] = x.Count }).ToList(),
            ["edges_by_type"] = aristasPorType.Take(8)
                .Select(x => new Dictionary<string, object?> { ["type"] = x.Name, ["count"] = x.Count }).ToList(),
            ["objetos_con_dinamico_sin_resolver"] = ciegos,
        };
        if (truncated) result["truncated"] = true;

        if (meta.TryGetValue(StoreSchema.MetaKeys.GeneratedAt, out var genRaw) &&
            DateTimeOffset.TryParse(genRaw, out var generadoEn))
        {
            var dias = (int)(DateTimeOffset.Now - generadoEn).TotalDays;
            result["dias_desde_generado"] = dias;
            if (dias > 30)
                result["aviso"] = $"el store se generó hace {dias} días: puede no reflejar el estado actual de la base. Considera regenerarlo.";
        }

        return result;
    }

    private enum EscalarTipo { Texto, Entero, Booleano }

    private static readonly (string Column, EscalarTipo Tipo)[] ObjectScalarColumns =
    [
        ("object_type", EscalarTipo.Texto),
        ("schema_name", EscalarTipo.Texto),
        ("cyclomatic_complexity", EscalarTipo.Entero),
        ("total_steps", EscalarTipo.Entero),
        ("dynamic_sql_steps", EscalarTipo.Entero),
        ("unresolved_dynamic_sql_steps", EscalarTipo.Entero),
        ("max_nesting", EscalarTipo.Entero),
        ("has_error_handling", EscalarTipo.Booleano),
        ("has_cursor", EscalarTipo.Booleano),
        ("has_transaction", EscalarTipo.Booleano),
    ];

    /// <summary>
    /// La ficha de un SqlObject: lo que resolve_object no puede darte porque solo apunta
    /// ids, y lo que impact tampoco porque solo camina aristas. Escalares del nodo (NULLs
    /// omitidos), tablas leídas/escritas y a quién llama / quién lo llama.
    /// </summary>
    public static Dictionary<string, object?> DescribeObject(SqliteConnection conn, string id, int limit)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new McpToolException("describe_object: 'id' no puede estar vacío.");
        if (limit <= 0) limit = 10;
        limit = Math.Min(limit, 200);

        string label, name;
        var columnasSql = string.Join(", ", ObjectScalarColumns.Select(c => c.Column));
        var escalares = new Dictionary<string, object?>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"SELECT label, name, {columnasSql} FROM nodes WHERE id = $id LIMIT 1";
            cmd.Parameters.AddWithValue("$id", id);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                throw new McpToolException($"describe_object: no existe ningún nodo con id '{id}'. Resuélvelo antes con resolve_object.");

            label = reader.IsDBNull(0) ? "" : reader.GetString(0);
            name = reader.IsDBNull(1) ? id : reader.GetString(1);

            if (label != "SqlObject")
                throw new McpToolException(
                    $"describe_object: '{id}' es un nodo {label}, no un SqlObject. Para Table usa impact; para Column usa column_impact o column_provenance.");

            for (var i = 0; i < ObjectScalarColumns.Length; i++)
            {
                var (columna, tipo) = ObjectScalarColumns[i];
                var ordinal = 2 + i;
                if (reader.IsDBNull(ordinal)) continue;
                escalares[columna] = tipo switch
                {
                    EscalarTipo.Texto => reader.GetString(ordinal),
                    EscalarTipo.Booleano => reader.GetInt32(ordinal) != 0,
                    _ => (object)reader.GetInt64(ordinal),
                };
            }
        }

        var result = new Dictionary<string, object?> { ["id"] = id, ["name"] = name };
        foreach (var kv in escalares) result[kv.Key] = kv.Value;

        var truncated = false;
        AgregarLista(result, "tablas_leidas", NombresDeAristas(conn, id, "READS_FROM", srcEsObjeto: false), limit, ref truncated);
        AgregarLista(result, "tablas_escritas", NombresDeAristas(conn, id, "WRITES_TO", srcEsObjeto: false), limit, ref truncated);
        AgregarLista(result, "llama_a", NombresDeAristas(conn, id, "CALLS", srcEsObjeto: true), limit, ref truncated);
        AgregarLista(result, "llamado_por", NombresDeAristasInversa(conn, id, "CALLS"), limit, ref truncated);
        if (truncated) result["truncated"] = true;

        return result;
    }

    private static void AgregarLista(Dictionary<string, object?> result, string clave, List<string> nombres, int limit, ref bool truncated)
    {
        var ordenados = nombres.Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        result[clave] = ordenados.Take(limit).ToList();
        if (ordenados.Count > limit) truncated = true;
    }

    // WRITES_TO/READS_FROM llevan src granular a Step ("<objId>#stepN"); CALLS engancha
    // el SqlObject directamente en src.
    private static List<string> NombresDeAristas(SqliteConnection conn, string id, string type, bool srcEsObjeto)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = srcEsObjeto
            ? "SELECT DISTINCT dst FROM edges WHERE src = $id AND type = $type"
            : "SELECT DISTINCT dst FROM edges WHERE (src = $id OR src LIKE $id || '#%') AND type = $type";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$type", type);
        var ids = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) ids.Add(reader.GetString(0));

        var info = LookupNodes(conn, ids);
        return ids.Select(i => info.TryGetValue(i, out var v) ? v.Name : i).ToList();
    }

    private static List<string> NombresDeAristasInversa(SqliteConnection conn, string id, string type)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT src FROM edges WHERE dst = $id AND type = $type";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$type", type);
        var ids = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) ids.Add(reader.GetString(0));

        var info = LookupNodes(conn, ids);
        return ids.Select(i => info.TryGetValue(i, out var v) ? v.Name : i).ToList();
    }

    private static Dictionary<string, string> ReadMeta(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT key, value FROM meta";
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result[reader.GetString(0)] = reader.IsDBNull(1) ? "" : reader.GetString(1);
        return result;
    }

    private static object? ParseIntOr(string? s) => int.TryParse(s, out var n) ? n : s;

    private static int ScalarInt(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static List<(string Name, int Count)> GroupCount(SqliteConnection conn, string table, string column)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {column}, COUNT(*) FROM {table} GROUP BY {column} ORDER BY COUNT(*) DESC, {column} ASC";
        var result = new List<(string, int)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result.Add((reader.IsDBNull(0) ? "" : reader.GetString(0), reader.GetInt32(1)));
        return result;
    }

    private static Dictionary<string, object?> ColumnChain(SqliteConnection conn, string id, int depth, int limit, bool haciaFuentes)
    {
        var nombre = haciaFuentes ? "column_provenance" : "column_impact";
        ValidarColumna(conn, id, nombre, out _);
        if (depth < 1 || depth > 20) throw new McpToolException($"{nombre}: depth debe estar entre 1 y 20.");
        if (limit <= 0) limit = 20;
        limit = Math.Min(limit, 200);

        var encontrados = RecorrerDerives(conn, id, depth, haciaFuentes);
        // Más profundo primero: es el orden en que hay que arreglar, no el de descubrimiento.
        var ordenados = encontrados.OrderByDescending(e => e.Hops).ThenBy(e => e.Id, StringComparer.Ordinal).ToList();
        var pagina = ordenados.Take(limit)
            .Select(e => new Dictionary<string, object?> { ["id"] = e.Id, ["hops"] = e.Hops })
            .ToList();

        var result = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["depth"] = depth,
            ["sources"] = pagina,
            ["total"] = ordenados.Count,
        };
        if (ordenados.Count > limit) result["truncated"] = true;

        if (ordenados.Count == 0)
        {
            result["reason"] = "sin aristas DERIVES_FROM: el valor de esta columna no se calcula a partir de otras en este grafo (columna base, o el cálculo no se pudo resolver).";
            var contraria = RecorrerDerives(conn, id, 1, !haciaFuentes).Count;
            if (contraria > 0)
                result["hint"] = haciaFuentes
                    ? $"0 fuentes, pero {contraria} columna(s) SÍ se calculan a partir de esta - prueba column_impact."
                    : $"0 derivadas, pero esta columna SÍ se calcula a partir de {contraria} - prueba column_provenance.";
        }
        return result;
    }

    /// <summary>BFS sobre DERIVES_FROM. haciaFuentes: src=frontera, se recogen dst (de
    /// dónde viene). Al revés: dst=frontera, se recogen src (quién la consume).</summary>
    private static List<(string Id, int Hops)> RecorrerDerives(SqliteConnection conn, string id, int depth, bool haciaFuentes)
    {
        var desde = haciaFuentes ? "src" : "dst";
        var hacia = haciaFuentes ? "dst" : "src";

        var visitados = new HashSet<string>(StringComparer.Ordinal) { id };
        var frontera = new List<string> { id };
        var salida = new List<(string, int)>();

        for (var hop = 1; hop <= depth && frontera.Count > 0 && salida.Count < SafetyCap; hop++)
        {
            var siguiente = new List<string>();
            for (var offset = 0; offset < frontera.Count; offset += SqliteInBatchSize)
            {
                var lote = frontera.Skip(offset).Take(SqliteInBatchSize).ToList();
                using var cmd = conn.CreateCommand();
                var marcas = string.Join(",", lote.Select((_, i) => $"$p{i}"));
                cmd.CommandText = $"SELECT DISTINCT {hacia} FROM edges WHERE {desde} IN ({marcas}) AND type = 'DERIVES_FROM'";
                for (var i = 0; i < lote.Count; i++) cmd.Parameters.AddWithValue($"$p{i}", lote[i]);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var otro = reader.GetString(0);
                    if (!visitados.Add(otro)) continue;   // guarda de ciclos: una columna calculada de sí misma no cuelga
                    siguiente.Add(otro);
                    salida.Add((otro, hop));
                }
            }
            frontera = siguiente;
        }
        return salida;
    }

    private static void ValidarColumna(SqliteConnection conn, string id, string herramienta, out string db)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new McpToolException($"{herramienta}: 'id' no puede estar vacío.");

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT label, db FROM nodes WHERE id = $id LIMIT 1";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            throw new McpToolException($"{herramienta}: no existe ningún nodo con id '{id}'. Resuélvelo antes con resolve_object.");

        var label = reader.IsDBNull(0) ? "" : reader.GetString(0);
        if (label != "Column")
            throw new McpToolException($"{herramienta}: '{id}' es un nodo {label}, no una Column. Para objetos y tablas usa impact.");

        // nodes.db viene NULL en los nodos Column (solo se rellena en SqlObject), así que
        // caer al prefijo del id: "Db:table:esquema.tabla:column:Col". Sin esto el descargo
        // de SQL dinámico no salía nunca y un 0 silencioso pasaba por "no hay riesgo".
        db = reader.IsDBNull(1) ? "" : reader.GetString(1);
        if (db.Length == 0)
        {
            var corte = id.IndexOf(':');
            if (corte > 0) db = id[..corte];
        }
    }

    private static int ContarDinamicoSinResolver(SqliteConnection conn, string db)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM nodes WHERE label = 'SqlObject' AND db = $db AND unresolved_dynamic_sql_steps > 0";
        cmd.Parameters.AddWithValue("$db", db);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static bool HasAnyEdge(SqliteConnection conn, string id, string matchColumn)
    {
        using var cmd = conn.CreateCommand();
        var typesCsv = string.Join(",", ImpactEdgeTypes.Select(t => $"'{t}'"));
        var whereMatch = matchColumn == "src" ? "(src = $id OR src LIKE $id || '#%')" : $"{matchColumn} = $id";
        cmd.CommandText = $"SELECT 1 FROM edges WHERE {whereMatch} AND type IN ({typesCsv}) LIMIT 1";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read();
    }

    private static IEnumerable<string> QueryNeighbors(SqliteConnection conn, List<string> ids, string matchColumn, string otherColumn)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var typesCsv = string.Join(",", ImpactEdgeTypes.Select(t => $"'{t}'"));
        for (var offset = 0; offset < ids.Count; offset += SqliteInBatchSize)
        {
            var batch = ids.Skip(offset).Take(SqliteInBatchSize).ToList();
            using var cmd = conn.CreateCommand();
            string whereMatch;
            if (matchColumn == "src")
            {
                // edges.src is Step-granular for READS_FROM/WRITES_TO/READS_COLUMN/
                // WRITES_COLUMN (a Step id is "<objId>#stepN"); CALLS is the only type
                // with a SqlObject directly on src. Match the object id itself or any
                // step owned by it.
                whereMatch = string.Join(" OR ", batch.Select((_, i) => $"(src = $p{i} OR src LIKE $p{i} || '#%')"));
            }
            else
            {
                whereMatch = $"{matchColumn} IN ({string.Join(",", batch.Select((_, i) => $"$p{i}"))})";
            }
            cmd.CommandText = $"SELECT DISTINCT {otherColumn} FROM edges WHERE ({whereMatch}) AND type IN ({typesCsv})";
            for (var i = 0; i < batch.Count; i++)
                cmd.Parameters.AddWithValue($"$p{i}", batch[i]);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var raw = reader.GetString(0);
                var v = otherColumn == "src" ? StoreSchema.RollUpStep(raw) : raw;
                if (seen.Add(v))
                    yield return v;
            }
        }
    }

    private static Dictionary<string, (string Label, string Name)> LookupNodes(SqliteConnection conn, IEnumerable<string> ids)
    {
        var idList = ids.ToList();
        var result = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        for (var offset = 0; offset < idList.Count; offset += SqliteInBatchSize)
        {
            var batch = idList.Skip(offset).Take(SqliteInBatchSize).ToList();
            using var cmd = conn.CreateCommand();
            var placeholders = string.Join(",", batch.Select((_, i) => $"$p{i}"));
            cmd.CommandText = $"SELECT id, label, name FROM nodes WHERE id IN ({placeholders})";
            for (var i = 0; i < batch.Count; i++)
                cmd.Parameters.AddWithValue($"$p{i}", batch[i]);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                result[reader.GetString(0)] = (reader.IsDBNull(1) ? "" : reader.GetString(1), reader.IsDBNull(2) ? "" : reader.GetString(2));
        }
        return result;
    }

    private static bool NodeExists(SqliteConnection conn, string id)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM nodes WHERE id = $id LIMIT 1";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read();
    }

    // 3 = exact (name or id); 2 = suffix at a "." or ":" boundary (e.g. needle
    // "orderlines" against id "...:table:sales.orderlines"); 1 = plain substring.
    private static int MatchScore(string id, string name, string needleLower)
    {
        var idLower = id.ToLowerInvariant();
        var nameLower = name.ToLowerInvariant();
        if (nameLower == needleLower || idLower == needleLower) return 3;
        if (EndsAtBoundary(nameLower, needleLower) || EndsAtBoundary(idLower, needleLower)) return 2;
        if (nameLower.Contains(needleLower) || idLower.Contains(needleLower)) return 1;
        return 0;
    }

    private static bool EndsAtBoundary(string haystack, string needle)
    {
        if (!haystack.EndsWith(needle, StringComparison.Ordinal)) return false;
        var i = haystack.Length - needle.Length;
        return i == 0 || haystack[i - 1] is '.' or ':';
    }

    private static string EscapeLike(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
