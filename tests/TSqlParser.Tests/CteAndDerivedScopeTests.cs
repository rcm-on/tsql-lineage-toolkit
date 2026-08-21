using TSqlParser;

namespace TSqlParser.Tests;

/// <summary>
/// EmitSubqueryReads resolvia por scope las subconsultas ScalarSubquery (EXISTS/IN/
/// comparacion escalar), pero NestedSubqueryCollector solo recogia ESE tipo de nodo: ni
/// una tabla derivada ("(SELECT ...) alias" en un FROM, QueryDerivedTable) ni el cuerpo
/// de un CTE ("WITH x AS (SELECT ...)", CommonTableExpression) son un ScalarSubquery, asi
/// que sus columnas propias (lista SELECT, JOIN...ON) no tenian scope donde resolverse.
///
/// Casos reales DNN: dbo.CoreMessaging_ConvertLegacyMessages (cursor sobre un CTE cuya
/// propia lista SELECT nombra 13 columnas literales) y dbo.Journal_ListForGroup (tabla
/// derivada con su propio JOIN...ON interno).
///
/// El seguimiento de CTE esta restringido a un DECLARE CURSOR (ver el porque largo en
/// AstWalker.EmitSubqueryReads): fuera de un cursor el WHERE del CTE ya lo cubre
/// EmitCteFilterSteps y sus columnas propias ya las cubre el flattening normal de la
/// sentencia que lo consume, así que seguirlo ahi tambien solo producia pasos
/// redundantes (CteUnionFilterTests.Governs_IsUnchangedByFilterExtraction lo destapo).
/// </summary>
public class CteAndDerivedScopeTests
{
    private const string Db = "TestDb";

    private static GraphPayload BuildGraph(string sql)
    {
        var result = SqlAnalyzer.AnalyzeObject($"{Db}::dbo.TestProc", sql);
        Assert.Null(result.Error);
        return GraphExporter.Build(new List<ObjectResult> { result }, includeColumns: true);
    }

    private static bool ReadsColumn(GraphPayload g, string table, string column) =>
        g.Relationships.Any(r => r.Type == "READS_COLUMN" &&
            g.Nodes.Any(n => n.Id == r.EndNodeId &&
                n.Labels.Contains("Column") &&
                string.Equals((string)n.Properties["table"], table, StringComparison.OrdinalIgnoreCase) &&
                string.Equals((string)n.Properties["name"], column, StringComparison.OrdinalIgnoreCase)));

    [Fact]
    public void CteBody_OwnSelectList_ResolvedInsideCursor()
    {
        // Mirrors dbo.CoreMessaging_ConvertLegacyMessages: a CTE's own SELECT list,
        // consumed by a "SELECT * FROM cte" embedded in a DECLARE CURSOR (the one
        // statement type GetStatementCtes never recognizes, so neither CollectCteNames
        // nor EmitCteFilterSteps ever see this WITH).
        var g = BuildGraph("""
            CREATE PROCEDURE dbo.TestProc
            AS
            BEGIN
                DECLARE c CURSOR FOR
                WITH messageItems AS (
                    SELECT MessageID, FromUserName, ToUserName
                    FROM dbo.Messaging_Messages
                )
                SELECT * FROM messageItems;
                OPEN c;
                CLOSE c;
                DEALLOCATE c;
            END
            """);

        Assert.True(ReadsColumn(g, "dbo.messaging_messages", "MessageID"),
            "MessageID de la propia lista SELECT del CTE debe leerse de dbo.Messaging_Messages");
        Assert.True(ReadsColumn(g, "dbo.messaging_messages", "FromUserName"),
            "FromUserName de la propia lista SELECT del CTE debe leerse de dbo.Messaging_Messages");
        Assert.True(ReadsColumn(g, "dbo.messaging_messages", "ToUserName"),
            "ToUserName de la propia lista SELECT del CTE debe leerse de dbo.Messaging_Messages");
    }

    [Fact]
    public void CteBody_OwnSelectList_NotDuplicated_OutsideCursor()
    {
        // Control: el MISMO CTE, pero consumido por un SELECT normal (sin cursor) - ya
        // tiene cobertura (el SELECT externo flattenea "c" a dbo.T4 y ya emite Id), asi
        // que seguir el CTE aqui tambien solo anadiria un paso redundante. No hay una
        // forma directa de contar pasos desde fuera de AstWalker con este helper, pero
        // el propio recall (READS_COLUMN) no debe depender de esta rama para este caso.
        var g = BuildGraph("""
            CREATE PROCEDURE dbo.TestProc
            AS
            BEGIN
                WITH c AS (SELECT Id FROM dbo.T4 WHERE T4.Borrado = 0)
                SELECT Id FROM c;
            END
            """);

        Assert.True(ReadsColumn(g, "dbo.t4", "Id"), "Id debe seguir llegando via el SELECT externo");
    }

    [Fact]
    public void CteBody_SelfReference_InsideCursor_DoesNotDuplicateReadsFrom()
    {
        // Guardia de la deduplicacion en ScalarSubqueryTableCollector.AddTables: una CTE
        // recursiva referenciandose a si misma dentro de un cursor substituye el
        // self-ref via cteBaseTables bajo un alias DISTINTO al de la propia rama
        // ("t" vs el alias original del ancla), asi que sin deduplicar POR TABLA (no
        // por alias) dbo.T5 se colaba dos veces en Tables y BuildExtraReads duplicaba
        // el READS_FROM (BuildExtraReads solo deduplica sus propias entradas, no
        // contra el target/Tables[0]). El caso real que lo destapo
        // (CommunityEdgeCaseGateTests "recursive-cte", dbo.vOrgChart) es fuera de un
        // cursor - este es el equivalente DENTRO de uno, el unico contexto donde nuestro
        // seguimiento de CTE esta activo.
        var g = BuildGraph("""
            CREATE PROCEDURE dbo.TestProc
            AS
            BEGIN
                DECLARE c CURSOR FOR
                WITH r AS (
                    SELECT Id, 0 AS Nivel FROM dbo.T5 WHERE T5.Raiz = 1
                    UNION ALL
                    SELECT t.Id, r.Nivel + 1 FROM dbo.T5 t JOIN r ON t.Padre = r.Id
                )
                SELECT Id FROM r;
                OPEN c;
                CLOSE c;
                DEALLOCATE c;
            END
            """);

        var readsFromT5 = g.Relationships.Count(r => r.Type == "READS_FROM" &&
            g.Nodes.Any(n => n.Id == r.EndNodeId && n.Labels.Contains("Table") &&
                string.Equals((string)n.Properties["name"], "dbo.T5", StringComparison.OrdinalIgnoreCase)));

        Assert.Equal(1, readsFromT5);
    }

    [Fact]
    public void DerivedTable_OwnJoinOn_ResolvedAgainstOwnScope()
    {
        // Mirrors dbo.Journal_ListForGroup: el JOIN...ON DENTRO de la tabla derivada
        // nunca tenia scope donde resolverse (EmitDerivedTableFilterSteps solo cubre su
        // WHERE, nunca su JOIN...ON).
        var g = BuildGraph("""
            CREATE PROCEDURE dbo.TestProc
            AS
            BEGIN
                INSERT INTO @j
                SELECT j.journalid FROM (
                    SELECT DISTINCT js.JournalId FROM dbo.Journal AS j
                    INNER JOIN dbo.Journal_Security AS js ON js.JournalId = j.JournalId
                ) AS j;
            END
            """);

        Assert.True(ReadsColumn(g, "dbo.journal", "JournalId"),
            "j.JournalId del JOIN...ON interno debe atribuirse a dbo.Journal");
        Assert.True(ReadsColumn(g, "dbo.journal_security", "JournalId"),
            "js.JournalId del JOIN...ON interno debe atribuirse a dbo.Journal_Security");
    }

    [Fact]
    public void DerivedTable_TvfAlias_HasNoKnownColumns_IsDropped()
    {
        // Mirrors dbo.Journal_ListForGroup: "t" es un alias sobre una funcion con
        // valores de tabla (TVF), no una tabla real - CollectTableRefsInto SI la
        // registra (para que la funcion participe en el grafo por su nombre), pero no
        // conoce sus columnas de salida. "t.seckey" no debe atribuirse a nada.
        var g = BuildGraph("""
            CREATE PROCEDURE dbo.TestProc
            AS
            BEGIN
                INSERT INTO @j
                SELECT j.journalid FROM (
                    SELECT DISTINCT js.JournalId FROM dbo.Journal AS j
                    INNER JOIN dbo.Journal_Security AS js ON js.JournalId = j.JournalId
                    INNER JOIN dbo.Journal_User_Permissions(@PortalId, @UserId, 1) AS t
                        ON t.seckey = js.SecurityKey
                ) AS j;
            END
            """);

        Assert.True(ReadsColumn(g, "dbo.journal_security", "SecurityKey"),
            "SecurityKey (calificada por la tabla real js) si debe resolverse");
        Assert.False(g.Relationships.Any(r => r.Type == "READS_COLUMN" &&
            g.Nodes.Any(n => n.Id == r.EndNodeId && n.Labels.Contains("Column") &&
                string.Equals((string)n.Properties["name"], "seckey", StringComparison.OrdinalIgnoreCase))),
            "t.seckey no tiene columnas conocidas (t es una TVF) y debe descartarse, no forzarse");
    }
}
