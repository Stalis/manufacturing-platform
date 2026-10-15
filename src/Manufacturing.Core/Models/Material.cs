namespace Manufacturing.Core.Models;

public class Material
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public required string SKU { get; init; }
    public required string Name { get; init; }

    public required MeasurementUnit MeasurementUnit { get; init; }
}
