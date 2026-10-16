namespace Manufacturing.Core.Models;

public class BillOfMaterial
{
    public record Record(Guid MaterialId, int Quantity);

    public Guid ProductId { get; init; }
    public uint Revision { get; init; } = 1;
    public List<Record> Materials { get; init; } = [];
}
