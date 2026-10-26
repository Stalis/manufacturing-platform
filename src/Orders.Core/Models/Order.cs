using System.Text.Json.Serialization;

namespace Orders.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter<OrderStatus>))]
public enum OrderStatus
{
    Created,
    WaitingForReserve,
    Reserved,
    InProgress,
    Done,
    Error,
}

[JsonConverter(typeof(JsonStringEnumConverter<OrderTaskStatus>))]
public enum OrderTaskStatus
{
    Pending,
    InProgress,
    Completed,
    Failed,
    Stopped,
}

public class Order
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public OrderStatus Status { get; set; } = OrderStatus.Created;

    public string? ErrorMessage { get; set; }

    public List<OrderItem> Items { get; set; } = [];

    public int TotalTasksCount => Items.Sum(i => i.Tasks.Count);

    public int CompletedTasksCount => Items.Sum(i => i.Tasks.Count(t => t.Status == OrderTaskStatus.Completed));

    public int InProgressTasksCount => Items.Sum(i => i.Tasks.Count(t => t.Status == OrderTaskStatus.InProgress));

    public double ProgressPercentage => TotalTasksCount == 0
        ? 0
        : Math.Round((double)CompletedTasksCount / TotalTasksCount * 100, 2);

    public void RecalculateStatus()
    {
        var allTasks = Items.SelectMany(i => i.Tasks).ToList();
        if (allTasks.Count == 0)
        {
            return;
        }

        if (allTasks.Any(t => t.Status == OrderTaskStatus.Failed))
        {
            Status = OrderStatus.Error;
            ErrorMessage ??= allTasks.First(t => t.Status == OrderTaskStatus.Failed).LastMessage;
            return;
        }

        if (allTasks.All(t => t.Status == OrderTaskStatus.Completed))
        {
            Status = OrderStatus.Done;
            return;
        }

        if (allTasks.Any(t => t.Status is OrderTaskStatus.InProgress or OrderTaskStatus.Completed or OrderTaskStatus.Stopped))
        {
            Status = OrderStatus.InProgress;
        }
    }

    public class OrderItem
    {
        public Guid ProductId { get; set; }
        public uint Revision { get; set; }
        public Guid BOMRevision { get; set; }
        public int Quantity { get; set; }
        public List<OrderProductionTask> Tasks { get; set; } = [];

        public int CompletedQuantity => Tasks.Count(t => t.Status == OrderTaskStatus.Completed);
    }

    public class OrderProductionTask
    {
        public Guid TaskId { get; set; }
        public OrderTaskStatus Status { get; set; } = OrderTaskStatus.Pending;
        public string? LastMessage { get; set; }
    }
}
