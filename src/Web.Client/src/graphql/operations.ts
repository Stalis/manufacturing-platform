import { gql } from '@apollo/client';

export const ORDER_FIELDS = gql`
  fragment OrderFields on OrderGql {
    id
    status
    errorMessage
    totalTasksCount
    completedTasksCount
    inProgressTasksCount
    progressPercentage
    items {
      productId
      revision
      reservationId
      quantity
      completedQuantity
      product {
        id
        sku
        name
      }
      tasks {
        taskId
        status
        lastMessage
      }
    }
  }
`;

export const GET_DASHBOARD_DATA = gql`
  ${ORDER_FIELDS}
  query GetDashboardData {
    orders {
      ...OrderFields
    }
    products {
      id
      sku
      name
      billOfMaterial(revision: 1) {
        productId
        revision
        materials {
          materialId
          quantity
          material {
            id
            sku
            name
            measurementUnit
          }
        }
      }
    }
    materials {
      id
      sku
      name
      measurementUnit
    }
  }
`;

export const CREATE_ORDER = gql`
  ${ORDER_FIELDS}
  mutation CreateOrder($input: CreateOrderInput!) {
    createOrder(input: $input) {
      ...OrderFields
    }
  }
`;

export const ON_ORDER_UPDATED = gql`
  ${ORDER_FIELDS}
  subscription OnOrderUpdated {
    onOrderUpdated {
      ...OrderFields
    }
  }
`;

export const ON_PRODUCER_EVENT = gql`
  ${ORDER_FIELDS}
  subscription OnProducerEvent {
    onProducerEvent {
      taskId
      type
      message
      updatedOrder {
        ...OrderFields
      }
    }
  }
`;
