using EfApp;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddScoped<IOrderRepository, OrderRepository>();
builder.Services.AddDbContext<ShopContext>(o => o.UseSqlServer(builder.Configuration.GetConnectionString("Wwi")));

var app = builder.Build();
app.MapGet("/health", () => "ok");
app.Run();
