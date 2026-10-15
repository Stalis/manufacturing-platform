using Manufacturing.Core.Models;

namespace Catalog.Core.Models;

public class BillOfMaterialItem
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid ProductId { get; init; }
    public Product Product { get; init; } = null!;
    public required Guid MaterialId { get; init; }
    public Material Material { get; init; } = null!;
    public required int Quantity { get; init; }
}