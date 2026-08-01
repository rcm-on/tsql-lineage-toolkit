using Microsoft.AspNetCore.Mvc;

namespace EfApp;

[ApiController]
[Route("api/[controller]")]
public class OrdersController : ControllerBase
{
    private readonly IOrderRepository _repository;
    private readonly HttpClient _httpClient;

    public OrdersController(IOrderRepository repository, HttpClient httpClient)
    {
        _repository = repository;
        _httpClient = httpClient;
    }

    [HttpGet("big")]
    public List<Order> GetBigOrders()
    {
        return _repository.GetBigOrders();
    }

    [HttpPost]
    public async Task<IActionResult> CreateOrder(Order order)
    {
        if (order.Total > 0)
        {
            _repository.AddOrder(order);
        }
        await _httpClient.PostAsync("https://billing.internal/api/invoices", null);
        return Ok();
    }
}
