using Microsoft.AspNetCore.Mvc;
using Orders.Core.Models;
using Contract = Orders.Contracts;

namespace Orders.Api.Endpoints;

public static class OrdersEndpoints
{
    public static IServiceCollection AddOrdersEndpoints(this IServiceCollection services)
    {
        Contract.OrdersApiEndpoints.AddOrdersApi(services);
        services.AddScoped<Contract.IOrdersHandler, OrdersHandler>();
        return services;
    }

    public static IEndpointRouteBuilder MapOrdersEndpoints(this IEndpointRouteBuilder app)
    {
        Contract.OrdersApiEndpoints.MapOrdersApi(app);
        return app;
    }

    internal static ProblemDetails Problem(string detail) => new() { Detail = detail };

    // Contract and domain enums share member names; the database stores the names.
    internal static Contract.Order ToContract(Order order) =>
        new(order.Id,
            Enum.Parse<Contract.OrderStatus>(order.Status.ToString()),
            order.ErrorMessage,
            order.TotalTasksCount,
            order.CompletedTasksCount,
            order.InProgressTasksCount,
            order.ProgressPercentage,
            order.Items.Select(item => new Contract.OrderItem(
                item.ProductId,
                item.Revision,
                item.BOMRevision,
                item.Quantity,
                item.CompletedQuantity,
                item.Tasks.Select(task => new Contract.OrderTask(
                    task.TaskId,
                    Enum.Parse<Contract.OrderTaskStatus>(task.Status.ToString()),
                    task.LastMessage)).ToArray())).ToArray());
}
