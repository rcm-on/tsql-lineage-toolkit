using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;

namespace TSqlParser;

/// <summary>
/// Captures SQL Server execution plans via Extended Events
/// (sqlserver.query_post_execution_showplan, event_file target) and
/// reconstructs one ShowPlanXML file per procedure - including the tables
/// touched by its EXEC(@sql)/sp_executesql dynamic SQL, which static analysis
/// cannot resolve when the object name is built from a parameter.
///
/// Why XE and not Query Store / ring_buffer (measured, see notes/task-captor-xe.md):
///   - Query Store has the plan but loses attribution: the dynamic statement's
///     object_id is 0, indistinguishable from any other ad-hoc query.
///   - ring_buffer target: showplan_xml comes back empty for this event.
///   - event_file target: showplan_xml is populated AND the event carries
///     nest_level, which - within one session_id - reconstructs the parent
///     procedure of a dynamic statement structurally, not by guessing from
///     a timestamp.
///
/// Attribution rule (validated with a THREE-level proc-calls-proc-calls-EXEC(@sql)
/// probe, not just the original two-level case): within one session_id, walk
/// events in (timestamp, file_offset) order keeping a stack of open PROC frames
/// keyed by nest_level. A non-PROC event (object_type ADHOC or PREPARED - SQL
/// Server used PREPARED for a plan-cached EXEC(@sql) in the probe, not always
/// ADHOC, so both are treated as "unresolved dynamic SQL") attaches to the top
/// of the stack, i.e. the nearest still-open PROC frame with a smaller
/// nest_level. That is "the last PROC event of nest_level immediately inferior"
/// from the brief, generalized to arbitrary depth via a stack instead of a
/// fixed nest_level-minus-one lookup (equivalent for the validated cases, and
/// correct if a deeper level is ever skipped).
/// </summary>
public static class XePlanCaptor
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    // sqlcmd-style batch separator: GO alone on its own line (optionally with a
    // repeat count). Mirrors SqlFileLoader.BatchSeparator.
    private static readonly Regex BatchSeparator = new(
        @"^[ \t]*GO[ \t]*\d*[ \t\r]*$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline);

    internal sealed record CapturedEvent(
        long SessionId,
        int NestLevel,
        string ObjectType,
        long ObjectId,
        int SourceDatabaseId,
        string ObjectName,
        string PlanXml,
        DateTime Timestamp,
        long FileOffset
    );

    /// <summary>
    /// Runs the full capture lifecycle: create the XE session, start it, run the
    /// workload (from --exec-file, or wait for the user to run it manually),
    /// stop the session (required to flush event_file), read + correlate the
    /// events, emit one ShowPlanXML per procedure, then ALWAYS stop/drop the
    /// session and delete the .xel files - even if something upstream failed.
    /// query_post_execution_showplan is an expensive event; leaving the session
    /// running would degrade the server.
    /// </summary>
    public static int Run(string server, string database, string outputDir, string? execFile, int waitSeconds)
    {
        Directory.CreateDirectory(outputDir);
        var plansDir = Path.Combine(outputDir, "plans");
        Directory.CreateDirectory(plansDir);

        var sessionName = $"tsql_toolkit_plan_capture_{Guid.NewGuid():N}"[..48];
        var xelPrefix = Path.Combine(outputDir, "capture");
        var xelPattern = xelPrefix + "*.xel";

        using var masterConn = Connect(server, "master");
        if (masterConn == null)
        {
            Console.Error.WriteLine($"No se pudo conectar a {server}");
            return 1;
        }

        var sessionCreated = false;
        try
        {
            CreateSession(masterConn, sessionName, database, xelPrefix);
            sessionCreated = true;
            StartSession(masterConn, sessionName);

            if (execFile != null)
            {
                Console.WriteLine($"Ejecutando carga desde {execFile} contra {database}...");
                RunWorkloadFile(server, database, execFile);
            }
            else if (waitSeconds > 0)
            {
                Console.WriteLine($"Sesion XE '{sessionName}' activa sobre '{database}'. Ejecuta la carga ahora. Esperando {waitSeconds}s...");
                Thread.Sleep(waitSeconds * 1000);
            }
            else
            {
                Console.WriteLine($"Sesion XE '{sessionName}' activa sobre '{database}'. Ejecuta la carga y pulsa ENTER cuando termines...");
                Console.ReadLine();
            }
        }
        finally
        {
            // Guarda critica: parar y eliminar la sesion pase lo que pase.
            try
            {
                if (sessionCreated)
                {
                    StopSessionIfRunning(masterConn, sessionName);
                    DropSessionIfExists(masterConn, sessionName);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"AVISO: fallo al limpiar la sesion XE '{sessionName}': {ex.Message}. " +
                                         $"Verificar manualmente con: SELECT * FROM sys.server_event_sessions WHERE name = '{sessionName}'");
            }
        }

        List<CapturedEvent> events;
        try
        {
            events = ReadEvents(masterConn, xelPattern);
        }
        finally
        {
            DeleteXelFiles(outputDir);
        }

        var nameCache = new Dictionary<(int, long), (string, string)?>();
        var procedures = Correlate(events, (dbId, objId) => ResolveName(masterConn, nameCache, dbId, objId));
        var written = EmitPlanFiles(procedures, plansDir);

        var byType = events.GroupBy(e => e.ObjectType).Select(g => $"{g.Key}={g.Count()}");
        Console.WriteLine($"Eventos capturados: {events.Count} ({string.Join(", ", byType)})");
        Console.WriteLine($"Procedimientos con datos propios o de SQL dinamico atribuido: {procedures.Count}");
        Console.WriteLine($"Ficheros de plan emitidos en {plansDir}: {written}");
        return 0;
    }

    // ── session management ──────────────────────────────────────────────────

    private static void CreateSession(SqlConnection conn, string sessionName, string database, string xelPrefix)
    {
        DropSessionIfExists(conn, sessionName);

        var dbLiteral = EscapeLiteral(database);
        var pathLiteral = EscapeLiteral(xelPrefix);
        var sql = $"""
            CREATE EVENT SESSION [{sessionName}] ON SERVER
            ADD EVENT sqlserver.query_post_execution_showplan(
                SET collect_database_name = 1
                ACTION (sqlserver.session_id)
                WHERE sqlserver.database_name = N'{dbLiteral}'
            )
            ADD TARGET package0.event_file(
                SET filename = N'{pathLiteral}'
            )
            WITH (MAX_MEMORY = 4096 KB, TRACK_CAUSALITY = ON, STARTUP_STATE = OFF);
            """;
        using var cmd = new SqlCommand(sql, conn);
        cmd.ExecuteNonQuery();
    }

    private static void StartSession(SqlConnection conn, string sessionName)
    {
        using var cmd = new SqlCommand($"ALTER EVENT SESSION [{sessionName}] ON SERVER STATE = START;", conn);
        cmd.ExecuteNonQuery();
    }

    private static void StopSessionIfRunning(SqlConnection conn, string sessionName)
    {
        using var cmd = new SqlCommand("""
            IF EXISTS (SELECT 1 FROM sys.dm_xe_sessions WHERE name = @name)
                EXEC('ALTER EVENT SESSION [' + @name + '] ON SERVER STATE = STOP;');
            """, conn);
        cmd.Parameters.AddWithValue("@name", sessionName);
        cmd.ExecuteNonQuery();
    }

    private static void DropSessionIfExists(SqlConnection conn, string sessionName)
    {
        using var cmd = new SqlCommand("""
            IF EXISTS (SELECT 1 FROM sys.server_event_sessions WHERE name = @name)
                EXEC('DROP EVENT SESSION [' + @name + '] ON SERVER;');
            """, conn);
        cmd.Parameters.AddWithValue("@name", sessionName);
        cmd.ExecuteNonQuery();
    }

    private static void DeleteXelFiles(string outputDir)
    {
        foreach (var f in Directory.EnumerateFiles(outputDir, "capture*.xel"))
        {
            try { File.Delete(f); }
            catch (Exception ex) { Console.Error.WriteLine($"AVISO: no se pudo borrar {f}: {ex.Message}"); }
        }
    }

    private static string EscapeLiteral(string s) => s.Replace("'", "''");

    // ── workload execution ──────────────────────────────────────────────────

    private static void RunWorkloadFile(string server, string database, string path)
    {
        var text = File.ReadAllText(path);
        using var conn = Connect(server, database)
            ?? throw new InvalidOperationException($"No se pudo conectar a {server}/{database} para ejecutar la carga");
        foreach (var raw in BatchSeparator.Split(text))
        {
            var batch = raw.Trim();
            if (batch.Length == 0) continue;
            using var cmd = new SqlCommand(batch, conn) { CommandTimeout = 300 };
            cmd.ExecuteNonQuery();
        }
    }

    // ── reading + correlating events ────────────────────────────────────────

    private static List<CapturedEvent> ReadEvents(SqlConnection conn, string xelPattern)
    {
        var result = new List<CapturedEvent>();
        if (Directory.EnumerateFiles(Path.GetDirectoryName(xelPattern)!, Path.GetFileName(xelPattern)).Any() == false)
            return result;

        // SET QUOTED_IDENTIFIER ON is mandatory before any .value()/.query() XML
        // method call (error 1934 otherwise), and showplan_xml is nested XML, not
        // text: .value() on it returns an empty string. Must use .query() on the
        // wrapped element (see notes/task-captor-xe.md trap #1/#2).
        const string sql = """
            SET QUOTED_IDENTIFIER ON;
            ;WITH ev AS (
                SELECT event_data = CAST(event_data AS XML), file_offset
                FROM sys.fn_xe_file_target_read_file(@pattern, NULL, NULL, NULL)
            )
            SELECT
                session_id      = event_data.value('(event/action[@name="session_id"]/value)[1]', 'bigint'),
                nest_level      = event_data.value('(event/data[@name="nest_level"]/value)[1]', 'int'),
                object_type     = event_data.value('(event/data[@name="object_type"]/text)[1]', 'nvarchar(50)'),
                object_id       = event_data.value('(event/data[@name="object_id"]/value)[1]', 'bigint'),
                source_db_id    = event_data.value('(event/data[@name="source_database_id"]/value)[1]', 'int'),
                object_name     = event_data.value('(event/data[@name="object_name"]/value)[1]', 'nvarchar(400)'),
                plan_xml        = CAST(event_data.query('(event/data[@name="showplan_xml"]/value/*)[1]') AS NVARCHAR(MAX)),
                ts              = event_data.value('(event/@timestamp)[1]', 'datetime2'),
                file_offset     = file_offset
            FROM ev
            ORDER BY session_id, ts, file_offset;
            """;
        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 300 };
        cmd.Parameters.AddWithValue("@pattern", xelPattern);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(reader.GetOrdinal("plan_xml")))
                continue;
            result.Add(new CapturedEvent(
                SessionId: reader.GetInt64(reader.GetOrdinal("session_id")),
                NestLevel: reader.IsDBNull(reader.GetOrdinal("nest_level")) ? 0 : reader.GetInt32(reader.GetOrdinal("nest_level")),
                ObjectType: reader.IsDBNull(reader.GetOrdinal("object_type")) ? "" : reader.GetString(reader.GetOrdinal("object_type")),
                ObjectId: reader.IsDBNull(reader.GetOrdinal("object_id")) ? 0 : reader.GetInt64(reader.GetOrdinal("object_id")),
                SourceDatabaseId: reader.IsDBNull(reader.GetOrdinal("source_db_id")) ? 0 : reader.GetInt32(reader.GetOrdinal("source_db_id")),
                ObjectName: reader.IsDBNull(reader.GetOrdinal("object_name")) ? "" : reader.GetString(reader.GetOrdinal("object_name")),
                PlanXml: reader.GetString(reader.GetOrdinal("plan_xml")),
                Timestamp: reader.GetDateTime(reader.GetOrdinal("ts")),
                FileOffset: reader.GetInt64(reader.GetOrdinal("file_offset"))
            ));
        }
        return result;
    }

    internal sealed class ProcAccumulator
    {
        public required string Schema;
        public required string Name;
        public readonly List<XElement> Statements = new();
    }

    /// <summary>
    /// Correlates events into one accumulator per procedure identity
    /// (schema.name), attaching each procedure's own statements plus the
    /// statements of any dynamic SQL (non-PROC events) that ran nested inside
    /// it, per the nest_level stack rule described on the class doc comment.
    /// <paramref name="resolveName"/> maps (source_database_id, object_id) to
    /// (schema, name) for a PROC event - injected so this method is unit-testable
    /// without a live SQL Server connection.
    /// </summary>
    internal static List<ProcAccumulator> Correlate(
        List<CapturedEvent> events, Func<int, long, (string Schema, string Name)?> resolveName)
    {
        var procs = new Dictionary<(string Schema, string Name), ProcAccumulator>(
            new TupleIgnoreCaseComparer());

        foreach (var sessionGroup in events.GroupBy(e => e.SessionId))
        {
            // Stack of open PROC frames for this session, ordered outer-to-inner.
            var stack = new List<(int NestLevel, ProcAccumulator Acc)>();

            foreach (var ev in sessionGroup.OrderBy(e => e.Timestamp).ThenBy(e => e.FileOffset))
            {
                var stmt = ExtractStatement(ev.PlanXml);
                if (stmt == null)
                    continue;

                if (string.Equals(ev.ObjectType, "PROC", StringComparison.OrdinalIgnoreCase))
                {
                    var identity = resolveName(ev.SourceDatabaseId, ev.ObjectId);
                    if (identity == null)
                        continue; // object dropped/unresolvable between capture and read - skip rather than guess

                    // Close frames at this depth or deeper: a new PROC event at this
                    // nest_level means the previous occupant of that depth (if any)
                    // is done, and anything deeper than it is stale.
                    stack.RemoveAll(f => f.NestLevel >= ev.NestLevel);

                    var key = (identity.Value.Schema, identity.Value.Name);
                    if (!procs.TryGetValue(key, out var acc))
                        procs[key] = acc = new ProcAccumulator { Schema = identity.Value.Schema, Name = identity.Value.Name };
                    acc.Statements.Add(stmt);
                    stack.Add((ev.NestLevel, acc));
                }
                else
                {
                    // Not a named object: ADHOC or PREPARED dynamic SQL (the plan
                    // cache store observed, not "always ADHOC" - see class doc).
                    // Drop stale deeper frames, then attach to whatever PROC frame
                    // is now on top - the nearest still-open ancestor.
                    stack.RemoveAll(f => f.NestLevel >= ev.NestLevel);
                    if (stack.Count == 0)
                        continue; // top-level ad hoc batch, no procedure to attribute to
                    stack[^1].Acc.Statements.Add(stmt);
                }
            }
        }

        return procs.Values.ToList();
    }

    private sealed class TupleIgnoreCaseComparer : IEqualityComparer<(string Schema, string Name)>
    {
        public bool Equals((string Schema, string Name) a, (string Schema, string Name) b) =>
            string.Equals(a.Schema, b.Schema, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string Schema, string Name) t) =>
            HashCode.Combine(t.Schema.ToLowerInvariant(), t.Name.ToLowerInvariant());
    }

    private static (string Schema, string Name)? ResolveName(
        SqlConnection conn, Dictionary<(int, long), (string, string)?> cache, int dbId, long objectId)
    {
        var key = (dbId, objectId);
        if (cache.TryGetValue(key, out var cached))
            return cached;

        using var cmd = new SqlCommand("SELECT OBJECT_SCHEMA_NAME(@id, @db), OBJECT_NAME(@id, @db)", conn);
        cmd.Parameters.AddWithValue("@id", objectId);
        cmd.Parameters.AddWithValue("@db", dbId);
        using var reader = cmd.ExecuteReader();
        (string, string)? result = null;
        if (reader.Read() && !reader.IsDBNull(0) && !reader.IsDBNull(1))
            result = (reader.GetString(0), reader.GetString(1));
        cache[key] = result;
        return result;
    }

    /// <summary>Pulls the single &lt;StmtSimple&gt;/&lt;StmtCursor&gt; element out of one
    /// event's full &lt;ShowPlanXML&gt; document (each captured event is exactly one
    /// statement's plan).</summary>
    internal static XElement? ExtractStatement(string planXml)
    {
        if (string.IsNullOrWhiteSpace(planXml))
            return null;
        XDocument doc;
        try { doc = XDocument.Parse(planXml); }
        catch (System.Xml.XmlException) { return null; }

        return doc.Descendants().FirstOrDefault(e =>
            e.Name == Ns + "StmtSimple" || e.Name == Ns + "StmtCursor");
    }

    // ── emission ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes one ShowPlanXML file per procedure, wrapping its accumulated
    /// statements (own + attributed dynamic SQL) in the &lt;StmtProc&gt;&lt;StoredProc&gt;
    /// structure ExecutionPlanParser.ParseStmtProc recognizes - the same
    /// envelope already proven (against the FRK corpus) to match 7/7 procedures.
    /// </summary>
    internal static int EmitPlanFiles(List<ProcAccumulator> procs, string plansDir)
    {
        var written = 0;
        foreach (var acc in procs)
        {
            if (acc.Statements.Count == 0)
                continue;

            var statementsEl = new XElement(Ns + "Statements", acc.Statements);
            var storedProcEl = new XElement(Ns + "StoredProc",
                new XAttribute("Schema", $"[{acc.Schema}]"),
                new XAttribute("ProcName", $"[{acc.Name}]"),
                statementsEl);
            var stmtProcEl = new XElement(Ns + "StmtProc", storedProcEl);
            var batchStatementsEl = new XElement(Ns + "Statements", stmtProcEl);
            var batchEl = new XElement(Ns + "Batch", batchStatementsEl);
            var batchSeqEl = new XElement(Ns + "BatchSequence", batchEl);
            var root = new XElement(Ns + "ShowPlanXML", batchSeqEl);

            var fileName = SanitizeFileName($"{acc.Schema}.{acc.Name}") + ".xml";
            var path = Path.Combine(plansDir, fileName);
            new XDocument(root).Save(path);
            written++;
        }
        return written;
    }

    private static string SanitizeFileName(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            sb.Append(Path.GetInvalidFileNameChars().Contains(c) ? '_' : c);
        return sb.ToString();
    }

    private static SqlConnection? Connect(string server, string database)
    {
        var connStr = SqlConnections.Build(server, database, 15, SqlConnections.FromEnvironment());
        try
        {
            var conn = new SqlConnection(connStr);
            conn.Open();
            return conn;
        }
        catch (SqlException)
        {
            return null;
        }
    }
}
