namespace Orders.Core;

public class Order
{
    public record Record(Guid ProductId, int Quantity);
    public Guid Id { get; set; } = Guid.CreateVersion7();

}
