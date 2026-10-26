using Manufacturing.Core.Models;
using Orders.Core.Models;

namespace Orders.Core.Tests;

public class BomSnapshotTests
{
    [Fact]
    public void HashIsDeterministicRegardlessOfMaterialsOrder()
    {
        var productId = Guid.CreateVersion7();
        var materialA = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var materialB = Guid.Parse("00000000-0000-0000-0000-000000000002");

        var snapshot1 = new BomSnapshot
        {
            BillOfMaterial = new BillOfMaterial
            {
                ProductId = productId,
                Revision = 1,
                Materials =
                [
                    new BillOfMaterial.Record(materialB, 5),
                    new BillOfMaterial.Record(materialA, 2),
                ],
            },
        };

        var snapshot2 = new BomSnapshot
        {
            BillOfMaterial = new BillOfMaterial
            {
                ProductId = productId,
                Revision = 1,
                Materials =
                [
                    new BillOfMaterial.Record(materialA, 2),
                    new BillOfMaterial.Record(materialB, 5),
                ],
            },
        };

        Assert.Equal(snapshot1.Hash, snapshot2.Hash);
        Assert.Equal(snapshot1.Json, snapshot2.Json);

        var roundtripped = BomSnapshot.FromJson(snapshot1.Json);
        Assert.Equal(snapshot1.Hash, roundtripped.Hash);
        Assert.Equal(productId, roundtripped.BillOfMaterial.ProductId);
        Assert.Equal(2, roundtripped.BillOfMaterial.Materials.Count);
    }

    [Fact]
    public void OrderRecalculatesStatusAndProgressAcrossLifecycle()
    {
        var task1 = new Order.OrderProductionTask { TaskId = Guid.CreateVersion7() };
        var task2 = new Order.OrderProductionTask { TaskId = Guid.CreateVersion7() };
        var task3 = new Order.OrderProductionTask { TaskId = Guid.CreateVersion7() };
        var task4 = new Order.OrderProductionTask { TaskId = Guid.CreateVersion7() };

        var order = new Order
        {
            Status = OrderStatus.Reserved,
            Items =
            [
                new Order.OrderItem
                {
                    ProductId = Guid.CreateVersion7(),
                    Revision = 1,
                    Quantity = 4,
                    Tasks = [task1, task2, task3, task4],
                },
            ],
        };

        order.RecalculateStatus();
        Assert.Equal(OrderStatus.Reserved, order.Status);
        Assert.Equal(0, order.CompletedTasksCount);
        Assert.Equal(4, order.TotalTasksCount);
        Assert.Equal(0, order.ProgressPercentage);

        // First task starts -> Order moves to InProgress
        task1.Status = OrderTaskStatus.InProgress;
        order.RecalculateStatus();
        Assert.Equal(OrderStatus.InProgress, order.Status);
        Assert.Equal(1, order.InProgressTasksCount);
        Assert.Equal(0, order.ProgressPercentage);

        // First task completes -> Progress is 25%
        task1.Status = OrderTaskStatus.Completed;
        order.RecalculateStatus();
        Assert.Equal(OrderStatus.InProgress, order.Status);
        Assert.Equal(1, order.CompletedTasksCount);
        Assert.Equal(1, order.Items[0].CompletedQuantity);
        Assert.Equal(25, order.ProgressPercentage);

        // Remaining tasks complete -> Order moves to Done (100%)
        task2.Status = OrderTaskStatus.Completed;
        task3.Status = OrderTaskStatus.Completed;
        task4.Status = OrderTaskStatus.Completed;
        order.RecalculateStatus();
        Assert.Equal(OrderStatus.Done, order.Status);
        Assert.Equal(4, order.CompletedTasksCount);
        Assert.Equal(100, order.ProgressPercentage);

        // If any task fails -> Order moves to Error
        task3.Status = OrderTaskStatus.Failed;
        task3.LastMessage = "Failed to grab materials from inventory";
        order.RecalculateStatus();
        Assert.Equal(OrderStatus.Error, order.Status);
        Assert.Equal("Failed to grab materials from inventory", order.ErrorMessage);
    }
}
