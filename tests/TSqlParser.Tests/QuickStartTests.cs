using System.Text.Json;

namespace TSqlParser.Tests;

public class QuickStartTests
{
    private const string Db = @"C:\Users\Ana Pérez\out\graph_full.db";

    [Fact]
    public void McpLaunch_ConDotnet_AnteponeElEnsamblado()
    {
        var (command, args) = QuickStart.McpLaunch(@"C:\Program Files\dotnet\dotnet.exe", @"C:\app\TSqlParser.dll", Db);

        Assert.Equal(@"C:\Program Files\dotnet\dotnet.exe", command);
        Assert.Equal([@"C:\app\TSqlParser.dll", "mcp", "--store", Db], args);
    }

    [Fact]
    public void McpLaunch_ConApphost_UsaElEjecutableDirecto()
    {
        var (command, args) = QuickStart.McpLaunch(@"C:\app\TSqlParser.exe", @"C:\app\TSqlParser.dll", Db);

        Assert.Equal(@"C:\app\TSqlParser.exe", command);
        Assert.Equal(["mcp", "--store", Db], args);
    }

    [Fact]
    public void ConfigText_JsonEsParseableYConservaLaRuta()
    {
        var (command, args) = QuickStart.McpLaunch(@"C:\Program Files\dotnet\dotnet.exe", @"C:\app\TSqlParser.dll", Db);
        var text = QuickStart.ConfigText(command, args);

        var json = text[text.IndexOf('{')..];
        using var doc = JsonDocument.Parse(json);
        var server = doc.RootElement.GetProperty("mcpServers").GetProperty("tsql-lineage");
        Assert.Equal(command, server.GetProperty("command").GetString());
        Assert.Equal(args, server.GetProperty("args").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.Equal(Db, args[^1]);
    }

    [Fact]
    public void ConfigText_LineaClaudeMcpAdd_EntrecomillaRutasConEspacios()
    {
        var text = QuickStart.ConfigText(@"C:\app\TSqlParser.exe", ["mcp", "--store", Db]);

        Assert.Contains("claude mcp add tsql-lineage -- " + @"C:\app\TSqlParser.exe mcp --store " + "\"" + Db + "\"", text);
    }

    [Fact]
    public void ConfigText_AclaraQueElServidorLeeElDb()
    {
        var text = QuickStart.ConfigText(@"C:\app\TSqlParser.exe", ["mcp", "--store", Db]);

        Assert.Contains("no se conecta a SQL Server", text);
    }

    [Fact]
    public void Run_StoreInexistente_ExplicaComoGenerarlo()
    {
        var original = Console.Error;
        var capturado = new StringWriter();
        Console.SetError(capturado);
        try
        {
            var rc = new McpServer().Run(Path.Combine(Path.GetTempPath(), $"no-existe-{Guid.NewGuid():n}.db"));

            Assert.Equal(1, rc);
        }
        finally { Console.SetError(original); }

        Assert.Contains("TSqlParser quickstart <base>", capturado.ToString());
        Assert.Contains("--columns --sqlite", capturado.ToString());
    }
}
