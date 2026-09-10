using System.Text.Json;
using TSqlParser;

namespace TSqlParser.Tests;

/// <summary>
/// Gate de <see cref="SyntaxCoverage"/>: el motor tiene que DECLARAR lo que no sabe tratar.
///
/// El fallo que esto cierra no da error ni referencia ciega. Un tipo de nodo sin caso en el
/// recorrido produce un grafo incompleto con aspecto de completo, y eso es peor que un grafo
/// vacío porque el vacío se nota. Aquí se comprueba que el sumidero cuenta lo que llega al
/// <c>default</c>, que distingue lo benigno declarado de lo que no lo es, y — lo que de verdad
/// importa — que no se activa fuera de su ámbito.
/// </summary>
public class SyntaxCoverageTests
{
    private static string EscribirInput(params (string Nombre, string Sql)[] modulos)
    {
        var ruta = Path.Combine(Path.GetTempPath(), $"syntax-cov-{Guid.NewGuid():n}.json");
        var payload = modulos.Select(m => new { name = m.Nombre, sql = m.Sql });
        File.WriteAllText(ruta, JsonSerializer.Serialize(payload));
        return ruta;
    }

    [Fact]
    public void FueraDeAmbito_NoRecogeNada()
    {
        // El camino normal del motor no debe pagar por la instrumentación ni acumular estado
        // entre análisis: sin Collect() abierto, Record es un no-op.
        SqlAnalyzer.AnalyzeObject("db::dbo.p1", "CREATE PROCEDURE dbo.p1 AS BEGIN SET NOCOUNT ON; END");

        using var scope = SyntaxCoverage.Collect();
        Assert.Empty(scope.Rows("db::dbo.p1"));
    }

    [Fact]
    public void SetNoCount_SeDeclaraBenigno()
    {
        using var scope = SyntaxCoverage.Collect();
        SqlAnalyzer.AnalyzeObject("db::dbo.p1", "CREATE PROCEDURE dbo.p1 AS BEGIN SET NOCOUNT ON; END");

        var filas = scope.Rows("dbo.p1");
        var predicado = filas.FirstOrDefault(f => f.NodeType == "PredicateSetStatement");
        Assert.True(predicado.NodeType == "PredicateSetStatement",
            "SET NOCOUNT ON debería llegar al default del recorrido y quedar registrado. Filas: " +
            string.Join(", ", filas.Select(f => f.NodeType)));
        Assert.True(predicado.Benign, "SET NOCOUNT ON no mueve datos: tiene que constar como benigno.");
    }

    [Fact]
    public void ConstruccionSinCaso_SeCuentaComoNoBenigna()
    {
        // BULK INSERT no tiene caso en el switch de AstWalker.Walk, y es lineage de verdad: una
        // escritura sobre dbo.t1 que hoy no aparece en el grafo. Ni error, ni ciega, ni aviso.
        using var scope = SyntaxCoverage.Collect();
        SqlAnalyzer.AnalyzeObject("db::dbo.p1", @"
CREATE PROCEDURE dbo.p1 AS
BEGIN
    BULK INSERT dbo.t1 FROM 'x' WITH (FIELDTERMINATOR = ',');
END");

        var filas = scope.Rows("dbo.p1");
        Assert.Contains(filas, f => !f.Benign && f.Family == SyntaxCoverage.FamiliaSentencia);
    }

    [Fact]
    public void UnRecorridoLimpio_NoDeclaraNadaNoBenigno()
    {
        // Control de sensibilidad en el otro sentido: si esto empezara a fallar, el gate estaría
        // marcando como "no cubierto" lo que el motor sí trata, y la señal dejaría de valer.
        using var scope = SyntaxCoverage.Collect();
        SqlAnalyzer.AnalyzeObject("db::dbo.p1", @"
CREATE PROCEDURE dbo.p1 AS
BEGIN
    INSERT INTO dbo.t2 (c1) SELECT c1 FROM dbo.t1 WHERE c2 = 1;
    UPDATE dbo.t3 SET c4 = 2 WHERE c5 = 3;
END");

        Assert.DoesNotContain(scope.Rows("dbo.p1"), f => !f.Benign);
    }

    [Fact]
    public void XmlNodes_NoSeCuentaComoNoCubierto()
    {
        // Encontrado por el ensayo del protocolo (notes/ensayo, 2026-09-10): "xmlCol.nodes(...)"
        // tiene la forma sintáctica de un TVF cualificado, la guarda del case lo excluye, y cae
        // al default — pero BuildXmlApplyMap SÍ lo resuelve a su columna base. El instrumento lo
        // declaraba "no cubierto" y mandó a un agente a reducir un defecto inexistente: las 21
        // apariciones que atribuyó al fallo eran las vistas XML de AdventureWorks.
        // Un instrumento que miente es peor que no tenerlo, porque se le hace caso.
        using var scope = SyntaxCoverage.Collect();
        SqlAnalyzer.AnalyzeObject("db::dbo.p1", @"
CREATE PROCEDURE dbo.p1 AS
BEGIN
    SELECT x1.c1
    FROM dbo.t1
    CROSS APPLY t1.xmlcol.nodes('/a/b') AS x1(c1);
END");

        Assert.DoesNotContain(scope.Rows("dbo.p1"),
            f => !f.Benign && f.NodeType == "SchemaObjectFunctionTableReference");
    }

    [Fact]
    public void Analyze_AtribuyeCadaNodoASuModulo()
    {
        var ruta = EscribirInput(
            ("db::dbo.limpio", "CREATE PROCEDURE dbo.limpio AS BEGIN SELECT c1 FROM dbo.t1; END"),
            ("db::dbo.sucio", "CREATE PROCEDURE dbo.sucio AS BEGIN BULK INSERT dbo.t1 FROM 'x'; END"));
        try
        {
            var filas = SyntaxCoverage.Analyze(ruta);

            Assert.Contains(filas, f => f.Module == "dbo.sucio" && !f.Benign);
            Assert.DoesNotContain(filas, f => f.Module == "dbo.limpio" && !f.Benign);
        }
        finally
        {
            File.Delete(ruta);
        }
    }

    [Fact]
    public void InformeAnonimo_NoPuedeLlevarNadaDelModeloDeDatos()
    {
        // La propiedad que hace útil todo esto: el informe no se anonimiza, es que NO PUEDE
        // llevar nada que anonimizar. Si alguien añade un campo con el nombre del módulo, del
        // objeto o un recorte de SQL "para dar contexto", este gate lo para antes de que salga
        // de una red ajena. Es la clase de fallo que no se detecta hasta que ya es una fuga.
        var ruta = EscribirInput(
            ("BaseDeFacturacion::ventas.CalculoComisionesAcme",
             "CREATE PROCEDURE ventas.CalculoComisionesAcme AS BEGIN " +
             "DECLARE @tv TABLE (importe int); " +
             "SELECT c.importe FROM ventas.FacturasClienteAcme c WHERE c.nifCliente = '12345678Z'; END"));
        try
        {
            var informe = SyntaxCoverage.AnalyzeAnonymous(ruta);
            var json = JsonSerializer.Serialize(informe);

            foreach (var prohibido in new[]
                     {
                         "BaseDeFacturacion", "ventas", "CalculoComisionesAcme",
                         "FacturasClienteAcme", "importe", "nifCliente", "12345678Z",
                     })
            {
                Assert.DoesNotContain(prohibido, json, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            File.Delete(ruta);
        }
    }

    [Fact]
    public void InformeAnonimo_LlevaLaFormaEntera_NoSoloLaColaDeFallos()
    {
        // Sin esto, un informe de tres filas describe una base de tres construcciones — y
        // cualquier cosa que se genere a partir de él es una caricatura: 100 % de casos límite
        // y ni una construcción normal alrededor. La forma es la mitad del diagnóstico que
        // faltaba: qué SÍ ve el motor, no solo qué se le escapa.
        var ruta = EscribirInput(("db::dbo.p1", @"
CREATE PROCEDURE dbo.p1 AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO dbo.t2 (c1) SELECT c1 FROM dbo.t1 WHERE c2 = 1;
    UPDATE dbo.t3 SET c4 = 2;
END"));
        try
        {
            var informe = SyntaxCoverage.AnalyzeAnonymous(ruta);
            var tipos = informe.Shape.Select(s => s.NodeType).ToList();

            // Construcciones que el motor SÍ trata: nunca aparecerían en Uncovered.
            Assert.Contains("InsertStatement", tipos);
            Assert.Contains("UpdateStatement", tipos);
            Assert.Contains("QuerySpecification", tipos);
            Assert.Contains("NamedTableReference", tipos);

            Assert.True(informe.Shape.Count > informe.Uncovered.Count,
                "La forma tiene que ser más rica que la cola de fallos; si no, no sirve para " +
                "generar nada. Forma: " + informe.Shape.Count + ", sin cubrir: " + informe.Uncovered.Count);
        }
        finally
        {
            File.Delete(ruta);
        }
    }

    [Fact]
    public void ElPerfil_TambienEsAnonimoPorConstruccion()
    {
        // La forma multiplica las filas del informe, así que multiplica la superficie por la
        // que podría escaparse un nombre. La comprobación tiene que cubrirla igual.
        var ruta = EscribirInput(("BaseFacturacion::ventas.ComisionesAcme",
            "CREATE PROCEDURE ventas.ComisionesAcme AS BEGIN SELECT importe FROM ventas.FacturasAcme; END"));
        try
        {
            var informe = SyntaxCoverage.AnalyzeAnonymous(ruta);
            Assert.NotEmpty(informe.Shape);
            Assert.Empty(SyntaxCoverage.VerifyAnonymous(informe));
            Assert.DoesNotContain("Acme", JsonSerializer.Serialize(informe), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(ruta);
        }
    }

    [Fact]
    public void VerifyAnonymous_CazaUnTextoQueNoSeaTipoDeScriptDom()
    {
        // La comprobación tiene que fallar cuando debe, no solo pasar cuando todo va bien: un
        // gate que no puede ponerse rojo no protege de nada.
        var contaminado = new SyntaxCoverage.AnonReport(
            "tsql-diag/2", "2026-09-10", 1, 0,
            new[] { new SyntaxCoverage.AnonNodeRow("statement", "FacturasClienteAcme", 1, 1, false) },
            Array.Empty<SyntaxCoverage.AnonParseError>(),
            Array.Empty<SyntaxCoverage.AnonNodeRow>());

        Assert.Contains("FacturasClienteAcme", SyntaxCoverage.VerifyAnonymous(contaminado));
    }

    [Fact]
    public void VerifyAnonymous_ApruebaUnInformeRealDelMotor()
    {
        var ruta = EscribirInput(("db::ventas.CalculoComisionesAcme",
            "CREATE PROCEDURE ventas.CalculoComisionesAcme AS BEGIN SET NOCOUNT ON; BULK INSERT ventas.t1 FROM 'x'; END"));
        try
        {
            Assert.Empty(SyntaxCoverage.VerifyAnonymous(SyntaxCoverage.AnalyzeAnonymous(ruta)));
        }
        finally
        {
            File.Delete(ruta);
        }
    }

    [Fact]
    public void InformeAnonimo_CuentaLosErroresPorNumeroYNoPorMensaje()
    {
        // El mensaje de ScriptDom cita el identificador que rompió ("Incorrect syntax near
        // 'Facturas'"); el número no cita nada. Por eso viaja el número.
        var ruta = EscribirInput(("db::dbo.roto", "CREATE PROCEDURE dbo.roto AS BEGIN SELECT FROM FacturasAcme; END"));
        try
        {
            var informe = SyntaxCoverage.AnalyzeAnonymous(ruta);

            Assert.Equal(1, informe.ModulesWithParseError);
            Assert.NotEmpty(informe.ParseErrors);
            Assert.DoesNotContain("Acme", JsonSerializer.Serialize(informe), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(ruta);
        }
    }

    [Fact]
    public void ElResumenNoVendeUnCeroComoCorreccion()
    {
        // Regla del cero culpable: "nada sin cubrir" no puede leerse como "el lineage es correcto".
        var texto = SyntaxCoverage.Summarize(new List<UncoveredNode>(), "x.csv");
        Assert.Contains("no significa que el lineage sea correcto", texto);
    }
}
