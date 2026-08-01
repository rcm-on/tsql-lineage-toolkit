using System.Data;
using Microsoft.Data.SqlClient;

namespace SampleApp.Api.Services;

/// <summary>
/// Patrón A: SqlCommand + StoredProcedure directo.
/// </summary>
public class OrderService
{
    private readonly string _connectionString;

    public OrderService(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("Wwi")
            ?? "Server=fake-sql-host;Database=WideWorldImporters;User Id=fake;Password=fake;";
    }

    public async Task<int> CreateOrders()
    {
        using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();

        using var cmd = new SqlCommand("Website.InsertCustomerOrders", conn)
        {
            CommandType = CommandType.StoredProcedure
        };

        return await cmd.ExecuteNonQueryAsync();
    }

    public async Task<int> RecordColdRoomReading()
    {
        using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();

        using var cmd = new SqlCommand("Website.RecordColdRoomTemperatures", conn)
        {
            CommandType = CommandType.StoredProcedure
        };

        return await cmd.ExecuteNonQueryAsync();
    }
}
