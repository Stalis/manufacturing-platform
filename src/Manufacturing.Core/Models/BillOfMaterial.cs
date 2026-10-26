namespace Manufacturing.Core.Models;

public class BillOfMaterial
{
    public Guid ProductId { get; init; }
    public uint Revision { get; init; } = 1;
    public List<Record> Materials { get; init; } = [];

    public record Record(Guid MaterialId, int Quantity);
}