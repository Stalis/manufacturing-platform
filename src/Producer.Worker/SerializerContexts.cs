using System.Text.Json.Serialization;
using Inventory.Contracts.Models;
using Producer.Contracts.Models;

namespace Producer.Worker;

[JsonSerializable(typeof(ProducerTaskV1))]
internal partial class ProducerTaskV1JsonContext : JsonSerializerContext;

[JsonSerializable(typeof(ProducerEventV1))]
internal partial class ProducerEventV1JsonContext : JsonSerializerContext;

[JsonSerializable(typeof(InventoryGrabTaskV1))]
internal partial class InventoryGrabTaskV1JsonContext : JsonSerializerContext;

[JsonSerializable(typeof(InventoryGrabReplyV1))]
internal partial class InventoryGrabReplyV1JsonContext : JsonSerializerContext;

[JsonSerializable(typeof(InventoryDeliveredEventV1))]
internal partial class InventoryDeliveredEventV1JsonContext : JsonSerializerContext;
