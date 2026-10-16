using System.Text.Json;
using System.Text.Json.Serialization;
using Manufacturing.Core.Models;

namespace Catalog.Seed;

public sealed record Package(string Name, string SkuPrefix);
public sealed record TransistorFamily(string Name, string SkuPrefix, int Count);
public sealed record Resistors(MeasurementUnit MeasurementUnit, int[] E24, int[] Decades, Package[] Packages);
public sealed record Transistors(MeasurementUnit MeasurementUnit, TransistorFamily[] Families);
public sealed record Wires(MeasurementUnit MeasurementUnit, int[] Awg, string[] Colors);
public sealed record Pcbs(MeasurementUnit MeasurementUnit, int[] Layers);
public sealed record Microcontrollers(MeasurementUnit MeasurementUnit, int[] Cores, int[] WordSizes);
public sealed record SeedItem(string Sku, int Quantity);
public sealed record SeedProduct(string Sku, string Name, SeedItem[] Materials, int Revision = 1);
public sealed record SeedMaterial(string Sku, string Name, MeasurementUnit MeasurementUnit);
public sealed record SeedData(Resistors Resistors, Transistors Transistors, Wires Wires,
    Pcbs Pcbs, Microcontrollers Microcontrollers, SeedProduct[] Products)
{
    public static SeedData Parse(string json) => JsonSerializer.Deserialize<SeedData>(json, new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter<MeasurementUnit>(allowIntegerValues: false) }
    }) ?? throw new InvalidDataException("Seed data must be an object.");
}
