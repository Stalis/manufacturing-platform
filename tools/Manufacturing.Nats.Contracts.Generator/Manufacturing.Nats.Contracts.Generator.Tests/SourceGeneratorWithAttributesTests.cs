using Xunit;

namespace Manufacturing.Nats.Contracts.Generator.Tests;

public class StringExtensionsTests
{
    [Theory]
    [InlineData("producer-worker", "ProducerWorker")]
    [InlineData("producer-worker-inventory", "ProducerWorkerInventory")]
    [InlineData("started_v1", "StartedV1")]
    [InlineData("", "")]
    public void ToPascalCaseConvertsHyphensAndUnderscores(string input, string expected)
    {
        Assert.Equal(expected, input.ToPascalCase());
    }

    [Theory]
    [InlineData("Producer::Worker", "ProducerWorker")]
    [InlineData("inventory-worker", "InventoryWorker")]
    public void DurableNameToClassNameStripsColonsAndConvertsToPascalCase(string input, string expected)
    {
        Assert.Equal(expected, input.DurableNameToClassName());
    }

    [Fact]
    public void IndentPrependsExpectedSpaces()
    {
        Assert.Equal("    test", "test".Indent(4));
        Assert.Equal("", "".Indent(4));
    }
}
