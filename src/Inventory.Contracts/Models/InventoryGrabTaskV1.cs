namespace Inventory.Contracts.Models;

public record InventoryGrabTaskV1(Guid ReservationId, Guid MaterialId, uint Quantity)
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
}