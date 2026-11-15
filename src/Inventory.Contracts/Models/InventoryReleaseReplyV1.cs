namespace Inventory.Contracts.Models;

public enum InventoryReleaseTaskStatus
{
    Released,
    NotFound,
}

public record InventoryReleaseReplyV1(Guid RequestId, InventoryReleaseTaskStatus Status);
