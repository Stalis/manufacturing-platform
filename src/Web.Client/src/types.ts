export type OrderStatus =
  | 'CREATED'
  | 'WAITING_FOR_RESERVE'
  | 'RESERVED'
  | 'IN_PROGRESS'
  | 'DONE'
  | 'ERROR';

export type OrderTaskStatus =
  | 'PENDING'
  | 'IN_PROGRESS'
  | 'COMPLETED'
  | 'FAILED'
  | 'STOPPED';

export interface Material {
  id: string;
  sku: string;
  name: string;
  measurementUnit: string;
}

export interface BillOfMaterialItem {
  materialId: string;
  quantity: number;
  material?: Material | null;
}

export interface BillOfMaterial {
  productId: string;
  revision: number;
  materials: BillOfMaterialItem[];
}

export interface Product {
  id: string;
  sku: string;
  name: string;
  billOfMaterial?: BillOfMaterial | null;
}

export interface OrderProductionTask {
  taskId: string;
  status: OrderTaskStatus;
  lastMessage?: string | null;
}

export interface OrderItem {
  productId: string;
  revision: number;
  reservationId: string;
  quantity: number;
  completedQuantity: number;
  product?: {
    id: string;
    sku: string;
    name: string;
  } | null;
  tasks: OrderProductionTask[];
}

export interface Order {
  id: string;
  status: OrderStatus;
  errorMessage?: string | null;
  totalTasksCount: number;
  completedTasksCount: number;
  inProgressTasksCount: number;
  progressPercentage: number;
  items: OrderItem[];
}

export interface ProducerEvent {
  taskId: string;
  type: string;
  message: string;
  updatedOrder?: Order | null;
}
