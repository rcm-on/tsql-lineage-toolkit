using TSqlParser;

namespace TSqlParser.Tests;

/// <summary>
/// Referencias entre bases de datos (nombres de 3 partes, "OtraBd.dbo.Tabla").
///
/// Por qué existe este fichero: el 2026-08-08 se comprobó a mano que el motor SÍ las modela
/// -sobre sp_Blitz, 19 tablas de msdb y 50 columnas- y acto seguido se comprobó que **nada lo
/// protegía**. Ninguno de los dos corpus gateados (DNN, WideWorldImportersDW) contiene una sola
/// referencia cross-database: medido con sys.sql_expression_dependencies, cero en ambos. Es
/// decir, se podía romper DetectCrossDb/ResolveCalleeKey y los 212 tests seguirían en verde.
/// Comportamiento verificado una vez y jamás gateado es comportamiento que se pierde.
///
/// LA TRAMPA, que va en un assert propio porque ya costó una conclusión falsa: el id del nodo es
/// "BaseDeAnalisis:table:otrabd.dbo.tabla". El prefijo es la base que se está ANALIZANDO, no la
/// base destino; el destino conserva su nombre de 3 partes dentro de la parte de tabla. Filtrar
/// nodos por el prefijo del id hace creer que no hay ninguna referencia cruzada — exactamente el
/// error que llevó a anunciar un gap inexistente.
/// </summary>
public class CrossDatabaseLineageTests
{
    private const string Db = "TestDb";

    private static GraphPayload BuildGraph(string sql)
    {
        var result = SqlAnalyzer.AnalyzeObject($"{Db}::dbo.TestProc", sql);
        Assert.Null(result.Error);
        return GraphExporter.Build(new List<ObjectResult> { result }, includeColumns: true);
    }

    private static List<string> TableIds(GraphPayload g) =>
        g.Nodes.Where(n => n.Labels.Contains("Table")).Select(n => n.Id).ToList();

    [Fact]
    public void CrossDatabaseRead_ProducesTableNodeQualifiedWithTargetDatabase()
    {
        var graph = BuildGraph(@"
            CREATE PROCEDURE dbo.TestProc AS
            SELECT b.database_name, b.backup_finish_date
            FROM msdb.dbo.backupset AS b;");

        var tables = TableIds(graph);

        // Control del cero culpable: si la extracción de tablas devolviera vacío, los Contains de
        // abajo fallarían por el motivo equivocado y el mensaje culparía al cross-database.
        Assert.True(tables.Count > 0, "El grafo no tiene ni un nodo :Table — la extracción se rompió antes de llegar al caso cross-database.");

        var target = tables.FirstOrDefault(id => id.Contains("msdb.dbo.backupset", StringComparison.OrdinalIgnoreCase));
        Assert.True(target != null,
            "No hay nodo de tabla para la referencia cross-database 'msdb.dbo.backupset'. Tablas emitidas:\n  " +
            string.Join("\n  ", tables));

        // La trampa, fijada: el prefijo del id es la base de ANALISIS (TestDb), no la destino.
        Assert.StartsWith($"{Db}:table:", target, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("msdb:table:", target, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CrossDatabaseRead_ProducesReadsFromEdge()
    {
        var graph = BuildGraph(@"
            CREATE PROCEDURE dbo.TestProc AS
            SELECT b.database_name FROM msdb.dbo.backupset AS b;");

        var target = TableIds(graph).FirstOrDefault(id => id.Contains("msdb.dbo.backupset", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(target);

        var reads = graph.Relationships.Where(r => r.Type == "READS_FROM" && r.EndNodeId == target).ToList();
        Assert.True(reads.Count > 0,
            "El nodo de la tabla cross-database existe pero nadie lee de él: sin READS_FROM, el análisis de " +
            "impacto responde que nada depende de msdb.dbo.backupset. Aristas READS_FROM del grafo: " +
            graph.Relationships.Count(r => r.Type == "READS_FROM"));
    }

    /// <summary>
    /// El lineage cross-database tiene que llegar a COLUMNA, no quedarse en la tabla: es la
    /// diferencia entre "este proc toca msdb" y "este proc se rompe si cambia backupset.type".
    /// </summary>
    [Fact]
    public void CrossDatabaseRead_ResolvesColumnLevelLineage()
    {
        var graph = BuildGraph(@"
            CREATE PROCEDURE dbo.TestProc AS
            SELECT b.database_name, b.type
            FROM msdb.dbo.backupset AS b
            WHERE b.backup_finish_date > '2020-01-01';");

        var cols = graph.Nodes
            .Where(n => n.Labels.Contains("Column") && n.Id.Contains("msdb.dbo.backupset", StringComparison.OrdinalIgnoreCase))
            .Select(n => n.Id.Split(":column:").Last().ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(cols.Contains("database_name") && cols.Contains("type"),
            "Faltan columnas leídas de la tabla cross-database. Columnas vistas: " +
            (cols.Count == 0 ? "(ninguna)" : string.Join(", ", cols.OrderBy(x => x))));
    }

    /// <summary>
    /// Control negativo. Sin él, un motor que metiera la base destino en TODA referencia -o que
    /// no distinguiera 2 partes de 3- pasaría los tests de arriba con nota. Una referencia de la
    /// misma base no debe quedar cualificada con nombre de base en la parte de tabla.
    /// </summary>
    [Fact]
    public void SameDatabaseRead_IsNotQualifiedAsCrossDatabase()
    {
        var graph = BuildGraph(@"
            CREATE PROCEDURE dbo.TestProc AS
            SELECT o.Id FROM dbo.Orders AS o;");

        var orders = TableIds(graph).FirstOrDefault(id => id.Contains("orders", StringComparison.OrdinalIgnoreCase));
        Assert.True(orders != null, "No se emitió la tabla dbo.Orders: el control no puede distinguir nada.");

        var tablePart = orders[(orders.IndexOf(":table:", StringComparison.Ordinal) + 7)..];
        Assert.Equal(2, tablePart.Split('.').Length);
    }

    /// <summary>
    /// El otro camino cross-database: EXEC a un procedimiento de otra base. Lo resuelve
    /// ResolveCalleeKey, que es código distinto de DetectCrossDb (uno conserva el nombre de 3
    /// partes, el otro le quita la base para casar con byPlainName), así que romper uno deja el
    /// otro en pie y hace falta cubrir los dos.
    /// </summary>
    [Fact]
    public void CrossDatabaseExec_BehavesLikeSameDatabaseExec_ToAnUnanalyzedTarget()
    {
        static int CallsIn(GraphPayload g) => g.Relationships.Count(r => r.Type == "CALLS");

        var crossDb = BuildGraph(@"
            CREATE PROCEDURE dbo.TestProc AS
            EXEC msdb.dbo.sp_send_dbmail @recipients = 'a@b.c';");

        // El control que hace falsable la afirmación: mismo EXEC, misma base, destino tampoco
        // analizado. Si este emitiera CALLS y el de arriba no, el factor sería cruzar de base y
        // habría un defecto. Si los dos se comportan igual, lo que manda es "destino no
        // analizado" y el cross-database no pinta nada.
        var sameDb = BuildGraph(@"
            CREATE PROCEDURE dbo.TestProc AS
            EXEC dbo.SomeUnanalyzedProc @x = 1;");

        Assert.True(CallsIn(crossDb) == CallsIn(sameDb),
            $"Cruzar de base cambia el resultado del EXEC: cross-db emite {CallsIn(crossDb)} aristas CALLS " +
            $"y el mismo caso en la misma base emite {CallsIn(sameDb)}. Eso sí sería un defecto de " +
            "cross-database, no la limitación conocida de 'destino no analizado'.");
    }
}
