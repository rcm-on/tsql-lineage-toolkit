using Microsoft.EntityFrameworkCore;

namespace SampleApp.Api.Services;

/// <summary>
/// DbContext mínimo, sin entidades reales: solo para habilitar Database.SqlQueryRaw
/// y Database.ExecuteSqlRawAsync (Patrón C).
/// </summary>
public class WwiContext : DbContext
{
    public WwiContext(DbContextOptions<WwiContext> options) : base(options)
    {
    }
}

/// <summary>
/// Patrón C: EF Core raw SQL.
/// </summary>
public class ReportingService
{
    private readonly WwiContext _context;

    public ReportingService(WwiContext context)
    {
        _context = context;
    }

    public async Task<List<OrderLineDto>> GetOrderLines(int orderId)
    {
        return await _context.Database
            .SqlQueryRaw<OrderLineDto>(
                "SELECT OrderLineID, Description FROM Sales.OrderLines WHERE OrderID = {0}",
                orderId)
            .ToListAsync();
    }

    public async Task<int> ApplyPaymentMethodUpdates(DateTime from, DateTime to)
    {
        return await _context.Database.ExecuteSqlRawAsync(
            "EXEC Integration.GetPaymentMethodUpdates @from, @to",
            from, to);
    }
}

public class OrderLineDto
{
    public int OrderLineID { get; set; }
    public string Description { get; set; } = string.Empty;
}
