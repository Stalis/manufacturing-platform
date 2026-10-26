using System.Text.Json.Serialization;
using Producer.Contracts.Models;

namespace Gateway.Api;

[JsonSerializable(typeof(ProducerEventV1))]
internal partial class ProducerEventV1JsonContext : JsonSerializerContext;
