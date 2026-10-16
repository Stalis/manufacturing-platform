using Orders.Core.Models;

namespace Orders.Api.Endpoints;

using Microsoft.AspNetCore.Routing;
using Orders.Api.Persistence;
using Orders.Core;

public static class OrdersEndpoints
{
    public static IEndpointRouteBuilder MapOrdersEndpoints(
        this IEndpointRouteBuilder app
    )
    {
        var group = app.MapGroup("/api/orders")
            .WithTags("Orders");

        group.MapGet("/{id:guid}", GetById)
            .WithName("GetOrderById")
            .Produces<Order>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);

        return app;
    }


    public static async Task<IResult> GetById(Guid Id, OrdersDbContext db, CancellationToken cs)
    {
        var order = await db.Orders.FindAsync([Id], cs);

        return order is not null ? Results.Ok(order) : Results.NotFound();
    }
}
