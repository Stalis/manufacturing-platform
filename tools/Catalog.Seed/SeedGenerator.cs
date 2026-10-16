using System.Globalization;

namespace Catalog.Seed;

public sealed record SeedCatalog(SeedMaterial[] Materials, SeedProduct[] Products)
{
    public void Validate()
    {
        var materials = Index(Materials, m => m.Sku, "seed material");
        Index(Products, p => p.Sku, "seed product");
        foreach (var material in Materials)
        {
            Check(!string.IsNullOrWhiteSpace(material.Name) && material.Name.Length <= 100, $"Invalid name: {material.Sku}");
            Check(Enum.IsDefined(material.MeasurementUnit), $"Invalid unit: {material.Sku}");
        }
        foreach (var product in Products)
        {
            Check(product.Revision > 0, $"Invalid revision: {product.Sku}");
            Check(!string.IsNullOrWhiteSpace(product.Name) && product.Name.Length <= 100, $"Invalid name: {product.Sku}");
            Check(product.Materials is { Length: > 0 }, $"Empty BOM: {product.Sku}");
            Index(product.Materials, item => item.Sku, $"BOM {product.Sku}");
            foreach (var item in product.Materials)
            {
                Check(item.Quantity > 0, $"Invalid quantity: {product.Sku}/{item.Sku}");
                Check(materials.ContainsKey(item.Sku), $"Unknown material: {product.Sku}/{item.Sku}");
            }
        }
    }

    public static Dictionary<string, T> Index<T>(IEnumerable<T> rows, Func<T, string> sku, string label)
    {
        var result = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            Check(row is not null, $"Null entry in {label}");
            var key = sku(row);
            Check(!string.IsNullOrWhiteSpace(key) && key == key.Trim(), $"Invalid SKU in {label}");
            Check(result.TryAdd(key, row), $"Duplicate SKU in {label}: {key}");
        }
        return result;
    }

    public static void Check([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}

public static class SeedGenerator
{
    public static SeedCatalog Generate(SeedData data)
    {
        var materials = new List<SeedMaterial>();
        foreach (var package in data.Resistors.Packages)
        {
            SeedCatalog.Check(package is not null && !string.IsNullOrWhiteSpace(package.Name)
                && !string.IsNullOrWhiteSpace(package.SkuPrefix), "Invalid resistor package");
            foreach (var decade in data.Resistors.Decades)
            foreach (var value in data.Resistors.E24)
            {
                SeedCatalog.Check(decade is >= 0 and <= 6 && value is >= 10 and <= 99, "Invalid resistor decade or nominal");
                var ohms = value * (int)Math.Pow(10, decade);
                var (label, suffix) = ResistorNominal(ohms);
                materials.Add(new($"{package.SkuPrefix}-{suffix}", $"Resistor {package.Name} {label}", data.Resistors.MeasurementUnit));
            }
        }
        foreach (var family in data.Transistors.Families)
        {
            SeedCatalog.Check(family is not null && family.Count is > 0 and <= 999
                && !string.IsNullOrWhiteSpace(family.Name) && !string.IsNullOrWhiteSpace(family.SkuPrefix), "Invalid transistor family");
            for (var number = 1; number <= family.Count; number++)
                materials.Add(new($"{family.SkuPrefix}-SYN-{number:000}", $"Synthetic {family.Name} Q-{number:000}", data.Transistors.MeasurementUnit));
        }
        SeedCatalog.Check(data.Wires.Awg.Length == 2 && data.Wires.Awg[0] >= 0
            && data.Wires.Awg[1] >= data.Wires.Awg[0] && data.Wires.Awg[1] <= 40, "AWG must be an inclusive range within 0–40");
        for (var awg = data.Wires.Awg[0]; awg <= data.Wires.Awg[1]; awg++)
        foreach (var color in data.Wires.Colors)
        {
            SeedCatalog.Check(!string.IsNullOrWhiteSpace(color), "Empty wire color");
            materials.Add(new($"WIRE-{awg}AWG-{color.ToUpperInvariant()}", $"Wire {awg} AWG {color}", data.Wires.MeasurementUnit));
        }
        foreach (var layers in data.Pcbs.Layers)
        {
            SeedCatalog.Check(layers > 0, "PCB layers must be positive");
            materials.Add(new($"PCB-{layers}L", $"PCB {layers}-layer", data.Pcbs.MeasurementUnit));
        }
        foreach (var cores in data.Microcontrollers.Cores)
        foreach (var bits in data.Microcontrollers.WordSizes)
        {
            SeedCatalog.Check(cores > 0 && bits > 0, "MCU cores and word sizes must be positive");
            materials.Add(new($"MCU-{cores}C-{bits}B", $"Microcontroller {cores}-core {bits}-bit", data.Microcontrollers.MeasurementUnit));
        }
        var catalog = new SeedCatalog(materials.ToArray(), data.Products);
        catalog.Validate();
        return catalog;
    }

    public static (string Label, string Suffix) ResistorNominal(int ohms)
    {
        var divisor = ohms >= 1_000_000 ? 1_000_000 : ohms >= 1_000 ? 1_000 : 1;
        var number = ((decimal)ohms / divisor).ToString("0.######", CultureInfo.InvariantCulture);
        var symbol = divisor == 1_000_000 ? "M" : divisor == 1_000 ? "K" : "";
        var unit = divisor == 1_000_000 ? "MOm" : divisor == 1_000 ? "kOm" : "Om";
        var suffix = number.Contains('.') ? number.Replace(".", symbol) : number + symbol;
        return ($"{number} {unit}", suffix);
    }
}
