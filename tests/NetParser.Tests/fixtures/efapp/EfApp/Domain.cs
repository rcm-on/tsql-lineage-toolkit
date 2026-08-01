using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace EfApp;

[Table("Customers", Schema = "Sales")]
public class Customer
{
    public int CustomerID { get; set; }
    public string CustomerName { get; set; } = "";
}

// Mapped by convention: DbSet property name "Orders" -> Sales.Orders (unique bare name).
public class Order
{
    public int OrderID { get; set; }
    public decimal Total { get; set; }
}

// Mapped fluently in ShopContext.OnModelCreating.
public class OrderLine
{
    public int OrderLineID { get; set; }
    public string Description { get; set; } = "";
}

public class ShopContext : DbContext
{
    public ShopContext(DbContextOptions<ShopContext> options) : base(options) { }

    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderLine> OrderLines => Set<OrderLine>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OrderLine>().ToTable("OrderLines", "Sales");
    }
}
