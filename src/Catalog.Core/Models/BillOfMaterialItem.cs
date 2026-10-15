using Manufacturing.Core.Models;

namespace Catalog.Core.Models;

public class BillOfMaterialItem
{
    public Guid RevisionId { get; init; }
    public BillOfMaterialRevision Revision { get; init; } = null!;
    public Product Product { get; init; } = null!;
    public required Guid MaterialId { get; init; }
    public Material Material { get; init; } = null!;
    public required int Quantity { get; set; }
}
