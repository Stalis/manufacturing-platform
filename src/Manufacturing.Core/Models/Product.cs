namespace Manufacturing.Core.Models;

public class Product
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public required string SKU { get; init; }
    public required string Name { get; set; }
}
