namespace Orders.Core.Tests;

public class ContractEnumTests
{
    // Orders.Api maps domain and contract statuses by name.
    [Fact]
    public void StatusNamesMatchDomain()
    {
        Assert.Equal(Enum.GetNames<Models.OrderStatus>().Order(), Enum.GetNames<Orders.Contracts.OrderStatus>().Order());
        Assert.Equal(Enum.GetNames<Models.OrderTaskStatus>().Order(),
                     Enum.GetNames<Orders.Contracts.OrderTaskStatus>().Order());
    }
}
