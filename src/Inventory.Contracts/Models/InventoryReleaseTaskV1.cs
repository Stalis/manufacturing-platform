namespace Inventory.Contracts.Models;

/// <summary>
/// Returns the unconsumed quantities of a reservation to stock. Repeated requests are no-ops.
/// </summary>
public record InventoryReleaseTaskV1(Guid ReservationId)
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
}
