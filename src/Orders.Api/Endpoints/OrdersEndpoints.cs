using Orders.Core.Models;
using Contract = Orders.Contracts;
using static Orders.Api.OrdersApiEndpoints;

namespace Orders.Api.Endpoints;

public static class OrdersEndpoints
{
    public static IServiceCollection AddOrdersEndpoints(this IServiceCollection services)
    {
        services.AddOrdersApi();
        services.AddScoped<IOrdersHandler, OrdersHandler>();
        return services;
    }

    // Contract and domain enums share member names; the database stores the names.
    internal static Contract.Order ToContract(Order order) =>
        new(order.Id,
            MapEnum<Contract.OrderStatus>(order.Status),
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
                    MapEnum<Contract.OrderTaskStatus>(task.Status),
                    task.LastMessage)).ToArray())).ToArray());
}
