namespace Inventory.Contracts.Models;

public enum InventoryGrabTaskStatus
{
    Success,
    ReservationIsEmpty,
    Failure,
}

public record InventoryGrabReplyV1(Guid RequestId, InventoryGrabTaskStatus Status, string? ErrorMessage);