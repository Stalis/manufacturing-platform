using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Orders.Core.Models;

namespace Gateway.Api.Clients;

public sealed record CreateOrderItemDto(Guid ProductId, uint Revision, int Quantity);

public sealed record CreateOrderDto(List<CreateOrderItemDto> Items);

public interface IOrdersClient
{
    Task<IReadOnlyList<Order>> GetAllOrdersAsync(CancellationToken ct = default);
    Task<Order?> GetOrderByIdAsync(Guid id, CancellationToken ct = default);
    Task<Order> CreateOrderAsync(CreateOrderDto request, CancellationToken ct = default);
}

public sealed class OrdersClient(HttpClient http) : IOrdersClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters =
        {
            new JsonStringEnumConverter<OrderStatus>(),
            new JsonStringEnumConverter<OrderTaskStatus>(),
        },
    };

    public async Task<IReadOnlyList<Order>> GetAllOrdersAsync(CancellationToken ct = default)
    {
        var orders = await http.GetFromJsonAsync<List<Order>>("/api/orders", JsonOptions, ct);
        return orders ?? [];
    }

    public async Task<Order?> GetOrderByIdAsync(Guid id, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"/api/orders/{id}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<Order>(JsonOptions, ct);
    }

    public async Task<Order> CreateOrderAsync(CreateOrderDto request, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync("/api/orders", request, JsonOptions, ct);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            var conflictOrder = await response.Content.ReadFromJsonAsync<Order>(JsonOptions, ct);
            if (conflictOrder is not null)
            {
                return conflictOrder;
            }
        }

        if (!response.IsSuccessStatusCode)
        {
            var details = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Orders API returned {(int)response.StatusCode}: {details}");
        }

        return await response.Content.ReadFromJsonAsync<Order>(JsonOptions, ct)
               ?? throw new InvalidOperationException("Orders API returned an empty order response.");
    }
}
