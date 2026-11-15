using Catalog.Api;
using Contract = Catalog.Contracts.MeasurementUnit;
using Storage = Manufacturing.Core.Models.MeasurementUnit;

namespace Catalog.Seed.Tests;

// Lives here because this project already references Catalog.Api, whose handlers use this mapping.
public class ContractEnumTests
{
    [Fact]
    public void MeasurementUnitsMapBetweenStorageAndContract()
    {
        foreach (var unit in Enum.GetValues<Storage>())
            Assert.Equal(unit, CatalogApiEndpoints.MapEnum<Storage>(CatalogApiEndpoints.MapEnum<Contract>(unit)));
        foreach (var unit in Enum.GetValues<Contract>())
            Assert.Equal(unit, CatalogApiEndpoints.MapEnum<Contract>(CatalogApiEndpoints.MapEnum<Storage>(unit)));
    }

    [Fact]
    public void UndefinedMeasurementUnitIsRejected() =>
        Assert.Throws<InvalidOperationException>(() => CatalogApiEndpoints.MapEnum<Contract>((Storage)99));
}
