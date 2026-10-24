using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace TSqlParser.Tests;

public class McpPromptsTests
{
    private static JsonElement Call(string method, string? paramsJson = null)
    {
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        var p = paramsJson == null ? "" : $",\"params\":{paramsJson}";
        var resp = new McpServer().HandleLine(conn, $"{{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"{method}\"{p}}}");
        Assert.NotNull(resp);
        return JsonDocument.Parse(resp!).RootElement.Clone();
    }

    // Gate: un prompt que cita una herramienta renombrada o inexistente manda al agente a un callejón.
    [Fact]
    public void CadaPrompt_CitaSoloHerramientasQueExisten_YTodasLasQueDeclara()
    {
        var registro = McpToolRegistry.Default.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var prompt in McpPrompts.Default)
        {
            var texto = prompt.Render(prompt.Arguments.ToDictionary(a => a.Name, _ => "dbo.X"));
            Assert.NotEmpty(prompt.Tools);
            foreach (var tool in prompt.Tools)
            {
                Assert.True(registro.Contains(tool), $"'{prompt.Name}' declara '{tool}', que no está en McpToolRegistry.Default.");
                Assert.True(texto.Contains(tool, StringComparison.Ordinal), $"'{prompt.Name}' declara '{tool}' pero su texto no lo cita.");
            }
        }
    }

    [Fact]
    public void PromptsList_DevuelveLosDos()
    {
        var prompts = Call("prompts/list").GetProperty("result").GetProperty("prompts");

        Assert.Equal(["assess_change", "onboard_database"],
            prompts.EnumerateArray().Select(p => p.GetProperty("name").GetString()!).OrderBy(n => n).ToArray());
        var assess = prompts.EnumerateArray().Single(p => p.GetProperty("name").GetString() == "assess_change");
        var arg = assess.GetProperty("arguments")[0];
        Assert.Equal("object", arg.GetProperty("name").GetString());
        Assert.True(arg.GetProperty("required").GetBoolean());
    }

    [Fact]
    public void PromptsGet_AssessChange_SinObject_Da32602()
    {
        var resp = Call("prompts/get", "{\"name\":\"assess_change\"}");

        Assert.Equal(-32602, resp.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public void PromptsGet_Desconocido_Da32602()
    {
        var resp = Call("prompts/get", "{\"name\":\"nope\"}");

        Assert.Equal(-32602, resp.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public void PromptsGet_AssessChange_IncluyeElValor()
    {
        var resp = Call("prompts/get", "{\"name\":\"assess_change\",\"arguments\":{\"object\":\"dbo.Orders.Status\"}}");

        var msg = resp.GetProperty("result").GetProperty("messages")[0];
        Assert.Equal("user", msg.GetProperty("role").GetString());
        Assert.Contains("dbo.Orders.Status", msg.GetProperty("content").GetProperty("text").GetString());
    }

    [Fact]
    public void PromptsGet_OnboardDatabase_NoNecesitaArgumentos()
    {
        var resp = Call("prompts/get", "{\"name\":\"onboard_database\"}");

        Assert.Contains("store_info", resp.GetProperty("result").GetProperty("messages")[0].GetProperty("content").GetProperty("text").GetString());
    }

    [Fact]
    public void Initialize_DeclaraPrompts()
    {
        var caps = Call("initialize").GetProperty("result").GetProperty("capabilities");

        Assert.True(caps.TryGetProperty("prompts", out _));
        Assert.True(caps.TryGetProperty("tools", out _));
    }
}
