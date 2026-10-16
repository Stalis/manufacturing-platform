namespace Orders.Core.Models;

public class Order
{
    public record OrderItem(Guid BOMRevision, int Quantity);

    public Guid Id { get; set; } = Guid.CreateVersion7();

    public List<OrderItem> Items { get; set; } = [];
}
