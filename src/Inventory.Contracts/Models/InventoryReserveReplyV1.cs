namespace Inventory.Contracts.Models;

public enum InventoryReserveTaskStatus
{
    Success,
    NotInStock,
    Error
}

public record InventoryReserveReplyV1(Guid RequestId, Guid? ReservationId, InventoryReserveTaskStatus Status, string? ErrorMessage);