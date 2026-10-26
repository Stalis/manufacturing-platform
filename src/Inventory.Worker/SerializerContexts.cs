using System.Text.Json.Serialization;
using Inventory.Contracts.Models;

namespace Inventory.Worker;

[JsonSerializable(typeof(InventoryGrabTaskV1))]
internal partial class InventoryGrabTaskV1JsonContext : JsonSerializerContext;

[JsonSerializable(typeof(InventoryGrabReplyV1))]
internal partial class InventoryGrabReplyV1JsonContext : JsonSerializerContext;

[JsonSerializable(typeof(InventoryReserveTaskV1))]
internal partial class InventoryReserveTaskV1JsonContext : JsonSerializerContext;

[JsonSerializable(typeof(InventoryReserveReplyV1))]
internal partial class InventoryReserveReplyV1JsonContext : JsonSerializerContext;

[JsonSerializable(typeof(InventoryDeliveredEventV1))]
internal partial class InventoryDeliveredEventV1JsonContext : JsonSerializerContext;
