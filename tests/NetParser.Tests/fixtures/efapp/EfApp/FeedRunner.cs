using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;

namespace EfApp;

/// <summary>Patrón D resoluble: el nombre del proc se concatena desde un parámetro
/// y los call sites pasan literales, así que el narrowing interprocedural aplica.</summary>
public class FeedRunner
{
    private readonly string _connectionString = "Server=fake;Database=WideWorldImporters;";

    public int Run(string feedName)
    {
        var proc = "Integration.Get" + feedName + "Updates";
        using var conn = new SqlConnection(_connectionString);
        using var cmd = new SqlCommand(proc, conn)
        {
            CommandType = CommandType.StoredProcedure
        };
        return cmd.ExecuteNonQuery();
    }

    public void Nightly()
    {
        Run("Order");
        Run("Sale");
    }

    public int Cleanup(bool force)
    {
        if (force)
        {
            using var conn = new SqlConnection(_connectionString);
            return conn.Execute("DELETE FROM Sales.SpecialDeals");
        }
        return 0;
    }
}
