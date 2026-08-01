using Microsoft.EntityFrameworkCore;
using SampleApp.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddScoped<OrderService>();
builder.Services.AddScoped<CustomerService>();
builder.Services.AddScoped<ReportingService>();
builder.Services.AddScoped<DynamicService>();

builder.Services.AddDbContext<WwiContext>(options =>
    options.UseSqlServer(
        builder.Configuration.GetConnectionString("Wwi")
            ?? "Server=fake-sql-host;Database=WideWorldImporters;User Id=fake;Password=fake;"));

var app = builder.Build();

// Configure the HTTP request pipeline.
app.UseHttpsRedirection();

// Patrón A — SqlCommand + StoredProcedure
app.MapPost("/api/orders", async (OrderService svc) => await svc.CreateOrders())
    .WithName("CreateOrders");

app.MapPost("/api/cold-room-readings", async (OrderService svc) => await svc.RecordColdRoomReading())
    .WithName("RecordColdRoomReading");

// Patrón B — Dapper
app.MapGet("/api/customers/search", async (string q, CustomerService svc) => await svc.SearchCustomers(q))
    .WithName("SearchCustomers");

app.MapGet("/api/customers/by-postal-code/{cityId:int}", async (int cityId, CustomerService svc) => await svc.GetCustomersByPostalCode(cityId))
    .WithName("GetCustomersByPostalCode");

// Patrón C — EF Core raw SQL
app.MapGet("/api/orders/{orderId:int}/lines", async (int orderId, ReportingService svc) => await svc.GetOrderLines(orderId))
    .WithName("GetOrderLines");

app.MapPost("/api/payment-methods/updates", async (DateTime from, DateTime to, ReportingService svc) => await svc.ApplyPaymentMethodUpdates(from, to))
    .WithName("ApplyPaymentMethodUpdates");

// Patrón D — nombre de procedimiento construido dinámicamente
app.MapPost("/api/integration-feeds/{feedName}", async (string feedName, DynamicService svc) => await svc.RunIntegrationFeed(feedName))
    .WithName("RunIntegrationFeed");

app.Run();
