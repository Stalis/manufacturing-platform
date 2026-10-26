using System.Text.Json.Serialization;
using Inventory.Contracts.Models;
using Producer.Contracts.Models;

namespace Orders.Api;

[JsonSerializable(typeof(ProducerTaskV1))]
internal partial class ProducerTaskV1JsonContext : JsonSerializerContext;

[JsonSerializable(typeof(ProducerEventV1))]
internal partial class ProducerEventV1JsonContext : JsonSerializerContext;

[JsonSerializable(typeof(InventoryReserveTaskV1))]
internal partial class InventoryReserveTaskV1JsonContext : JsonSerializerContext;

[JsonSerializable(typeof(InventoryReserveReplyV1))]
internal partial class InventoryReserveReplyV1JsonContext : JsonSerializerContext;
