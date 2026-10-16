using System.Globalization;
using System.Text.Json;
using Catalog.Seed;
using Manufacturing.Core.Models;

namespace Catalog.Seed.Tests;

public class SeedGeneratorTests
{
    internal static SeedData Data() => SeedData.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "catalog_seed.json")));

    [Fact]
    public void CompleteCatalogHasStableUniqueSkusAndUnits()
    {
        var catalog = SeedGenerator.Generate(Data());
        Assert.Equal(557, catalog.Materials.Length);
        Assert.Equal(557, catalog.Materials.Select(m => m.Sku).Distinct().Count());
        Assert.Equal(12, catalog.Products.Length);
        Assert.All(catalog.Materials, m => Assert.Equal(
            m.Sku.StartsWith("WIRE-") ? MeasurementUnit.Meter :
            m.Sku.StartsWith("PCB-") ? MeasurementUnit.SquareCentimeter : MeasurementUnit.Each, m.MeasurementUnit));
        Assert.Contains(catalog.Materials, m => m.Sku == "THT-R-100K" && m.Name == "Resistor THT 100 kOm");
        Assert.Equal(catalog.Materials, SeedGenerator.Generate(Data()).Materials);
    }

    [Theory]
    [InlineData("\"UnknownUnit\"")]
    [InlineData("5")]
    [InlineData("\"5\"")]
    public void RejectsInvalidUnits(string unit)
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "catalog_seed.json"));
        Assert.Throws<JsonException>(() => SeedData.Parse(json.Replace("\"Meter\"", unit)));
    }

    [Fact]
    public void ResistorFormattingMatchesOriginalAcrossEveryDecadeAndCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            foreach (var decade in Data().Resistors.Decades)
            foreach (var nominal in Data().Resistors.E24)
            {
                var ohms = nominal * (int)Math.Pow(10, decade);
                var (label, sku) = SeedGenerator.ResistorNominal(ohms);
                Assert.DoesNotContain(",", label);
                Assert.DoesNotContain("E+", label);
                var decoded = sku.Contains('M') ? decimal.Parse(sku.Replace('M', '.'), CultureInfo.InvariantCulture) * 1_000_000
                    : sku.Contains('K') ? decimal.Parse(sku.Replace('K', '.'), CultureInfo.InvariantCulture) * 1_000
                    : decimal.Parse(sku, CultureInfo.InvariantCulture);
                Assert.Equal(ohms, decoded);
            }
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void RejectsInvalidRecipesAndDuplicateSkus()
    {
        var catalog = SeedGenerator.Generate(Data());
        Assert.Throws<InvalidDataException>(() => (catalog with { Materials = [.. catalog.Materials, catalog.Materials[0]] }).Validate());
        Assert.Throws<InvalidDataException>(() => (catalog with { Products = [catalog.Products[0], catalog.Products[0]] }).Validate());
        foreach (var items in new SeedItem[][]
        {
            [new("missing", 1)], [new("PCB-2L", 0)], [new("PCB-2L", 1), new("PCB-2L", 2)], []
        })
            Assert.Throws<InvalidDataException>(() => (catalog with { Products = [catalog.Products[0] with { Materials = items }] }).Validate());
    }
}
