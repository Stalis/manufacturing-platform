namespace Inventory.Contracts.Models;

public record InventoryReserveTaskV1(Guid MaterialId, uint Quantity, Guid? ReservationId = null)
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
}

