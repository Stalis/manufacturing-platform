namespace Catalog.Seed.Tests;

public class ContractEnumTests
{
    // Catalog.Api maps storage and contract units by name.
    [Fact]
    public void MeasurementUnitNamesMatchStorage() =>
        Assert.Equal(Enum.GetNames<Manufacturing.Core.Models.MeasurementUnit>().Order(),
                     Enum.GetNames<Catalog.Contracts.MeasurementUnit>().Order());
}
