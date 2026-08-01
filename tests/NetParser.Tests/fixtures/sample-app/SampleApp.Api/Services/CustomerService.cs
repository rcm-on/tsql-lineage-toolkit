using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;

namespace SampleApp.Api.Services;

/// <summary>
/// Patrón B: Dapper (stored procedure y SQL inline).
/// </summary>
public class CustomerService
{
    private readonly string _connectionString;

    public CustomerService(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("Wwi")
            ?? "Server=fake-sql-host;Database=WideWorldImporters;User Id=fake;Password=fake;";
    }

    public async Task<IEnumerable<dynamic>> SearchCustomers(string searchText)
    {
        using var conn = new SqlConnection(_connectionString);
        return await conn.QueryAsync<dynamic>(
            "Website.SearchForCustomers",
            new { SearchText = searchText },
            commandType: CommandType.StoredProcedure);
    }

    public async Task<IEnumerable<dynamic>> GetCustomersByPostalCode(int cityId)
    {
        using var conn = new SqlConnection(_connectionString);
        return await conn.QueryAsync<dynamic>(
            "SELECT CustomerID, CustomerName FROM Sales.Customers WHERE PostalCityID = @cityId",
            new { cityId });
    }
}
