namespace Inventory.Contracts.Models;

public sealed record InventoryDeliveredEventV1(
    Guid ReservationId,
    Guid MaterialId,
    uint Quantity,
    bool IsReservationCompleted);
