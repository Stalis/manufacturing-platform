import "../tools/Contracts.Generator/http.tsp";

using Http;
using Manufacturing.CSharp;
using Manufacturing.Http;

/** Orders and their production progress. */
@service(#{ title: "Orders API" })
namespace Orders.Contracts;

enum OrderStatus {
  Created,
  WaitingForReserve,
  Reserved,
  InProgress,
  Done,
  Error,
}

enum OrderTaskStatus {
  Pending,
  InProgress,
  Completed,
  Failed,
  Stopped,
}

/** One production task; an order item has one task per unit. */
@csharpRecord(true)
model OrderTask {
  taskId: Guid;
  status: OrderTaskStatus;
  lastMessage: string | null;
}

@csharpRecord(true)
model OrderItem {
  productId: Guid;
  revision: uint32;
  reservationId: Guid;
  quantity: int32;
  completedQuantity: int32;
  tasks: OrderTask[];
}

@csharpRecord(true)
model Order {
  id: Guid;
  status: OrderStatus;
  errorMessage: string | null;
  totalTasksCount: int32;
  completedTasksCount: int32;
  inProgressTasksCount: int32;
  progressPercentage: float64;
  items: OrderItem[];
}

/** Revision must be positive; quantity is 1..10000 (one production task per unit). */
@csharpRecord(true)
model CreateOrderItem {
  productId: Guid;
  revision: uint32;
  quantity: int32;
}

@csharpRecord(true)
model CreateOrderRequest {
  items: CreateOrderItem[];
}

@route("/api/orders")
interface Orders {
  @get listOrders(): Order[];
  @get getOrder(@path id: Guid): Order | NotFound;

  /** Finds the order that owns a production task, as named in producer events. */
  @get
  @route("/by-task/{taskId}")
  getOrderByTask(@path taskId: Guid): Order | NotFound;

  /**
   * Resolves each item's bill of material and reserves materials. A reservation failure
   * still creates the order, with status Error and an error message, and releases the
   * reservations made for it.
   */
  @post createOrder(@body request: CreateOrderRequest): (CreatedResponse & {
    @body order: Order;
  }) | BadRequest | NotFound;
}
