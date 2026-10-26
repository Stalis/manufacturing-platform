namespace Catalog.Core.Models;

public class BillOfMaterialRevision
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid ProductId { get; init; }
    public uint Revision { get; init; } = 1;
}