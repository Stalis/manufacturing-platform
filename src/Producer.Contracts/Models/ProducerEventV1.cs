namespace Producer.Contracts.Models;

public enum ProducerEventType
{
    TaskStarted,
    TaskCompleted,
    TaskFailed,
    TaskInvalid,
    TaskStopped,
}

public sealed record ProducerEventV1(Guid TaskId, ProducerEventType Type, string Message);
