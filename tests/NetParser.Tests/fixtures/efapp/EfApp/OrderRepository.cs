namespace EfApp;

public interface IOrderRepository
{
    List<Order> GetBigOrders();
    void AddOrder(Order order);
}

public class OrderRepository : IOrderRepository
{
    private readonly ShopContext _context;

    public OrderRepository(ShopContext context)
    {
        _context = context;
    }

    public List<Order> GetBigOrders()
    {
        return _context.Orders.Where(o => o.Total > 100).ToList();
    }

    public void AddOrder(Order order)
    {
        _context.Orders.Add(order);
        _context.SaveChanges();
    }
}
