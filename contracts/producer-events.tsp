import "../tools/Contracts.Generator/decorators.tsp";

using Manufacturing.CSharp;

namespace Producer.Contracts.Models;

@format("uuid")
scalar Guid extends string;

enum ProducerEventType {
  TaskStarted: 0,
  TaskCompleted: 1,
  TaskFailed: 2,
  TaskInvalid: 3,
  TaskStopped: 4,
}

@doc("A production task state change.")
@csharpRecord(true)
model ProducerEventV1 {
  TaskId: Guid;
  Type: ProducerEventType;
  Message: string;
}
