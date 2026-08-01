using System.Data;
using Microsoft.Data.SqlClient;

namespace SampleApp.Api.Services;

/// <summary>
/// Patrón D: nombre de procedimiento construido dinámicamente (el caso difícil).
/// </summary>
public class DynamicService
{
    private readonly string _connectionString;

    public DynamicService(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("Wwi")
            ?? "Server=fake-sql-host;Database=WideWorldImporters;User Id=fake;Password=fake;";
    }

    public async Task<int> RunIntegrationFeed(string feedName)
    {
        // feedName ∈ {Order, Sale, Purchase}
        var proc = "Integration.Get" + feedName + "Updates";

        using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();

        using var cmd = new SqlCommand(proc, conn)
        {
            CommandType = CommandType.StoredProcedure
        };

        return await cmd.ExecuteNonQueryAsync();
    }
}
