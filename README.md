# Manufacturing Platform

A distributed, event-driven manufacturing management platform built with **.NET 10**, **NATS JetStream**, **PostgreSQL**, **HotChocolate GraphQL (BFF)**, and **React 19**.

---

## System Architecture

The platform is built around a **Contract-First** messaging design for NATS JetStream and a **Backend-for-Frontend (GraphQL API Gateway)** pattern for the client application:

- A single source-of-truth contract in [contracts/nats.contract.yaml](contracts/nats.contract.yaml) drives both:
  1. **Roslyn Incremental Source Generator** (`Manufacturing.Nats.Contracts.Generator`) — generates the strongly typed `NatsContract` C# class at compile time with all streams, subjects, and durable consumers.
  2. **Terraform** ([deploy/terraform/nats](deploy/terraform/nats)) — parses the exact same YAML file via the built-in `yamldecode` function to provision streams and durable consumers in NATS JetStream.

```mermaid
flowchart TB
    subgraph Client["Client Layer"]
        UI["Web.Client (React 19 + Vite + Apollo)"]
    end

    subgraph BFF["API Gateway (BFF)"]
        GW["Gateway.Api (.NET 10 + HotChocolate GraphQL)"]
    end

    subgraph Services["Microservices & Workers (.NET 10)"]
        CAT["Catalog.Api (Minimal API)"]
        ORD["Orders.Api (Minimal API + Progress Tracker)"]
        INV["Inventory.Worker (BackgroundService)"]
        PROD["Producer.Worker (BackgroundService)"]
    end

    subgraph Messaging["NATS JetStream"]
        JS_IC["Stream: INVENTORY_COMMANDS"]
        JS_IE["Stream: INVENTORY_EVENTS"]
        JS_PC["Stream: PRODUCTION_COMMANDS"]
        JS_PE["Stream: PRODUCTION_EVENTS"]
    end

    subgraph Storage["PostgreSQL 17"]
        DB_CAT[("DB: Catalog")]
        DB_ORD[("DB: Orders")]
        DB_INV[("DB: Inventory")]
    end

    subgraph Tooling["Contract-First & IaC"]
        YAML["contracts/nats.contract.yaml"]
        GEN["Roslyn Source Generator"]
        TF["Terraform (yamldecode)"]
    end

    UI -- "GraphQL Query / Mutation (HTTP)" --> GW
    UI <-- "GraphQL Subscription (WebSocket)" --> GW

    GW -- "REST (ICatalogClient)" --> CAT
    GW -- "REST (IOrdersClient)" --> ORD
    JS_PE -- "Consumer: gateway-api-producer" --> GW

    ORD -- "REST (ICatalogClient)" --> CAT
    ORD -- "Request/Reply: reserve.v1" --> JS_IC
    ORD -- "Publish: start.v1" --> JS_PC
    JS_PE -- "Consumer: orders-api-producer" --> ORD

    JS_IC -- "Consumer: inventory-worker" --> INV
    INV -- "Publish: delivered.v1" --> JS_IE

    JS_PC -- "Consumer: producer-worker" --> PROD
    PROD -- "Publish: grab.v1" --> JS_IC
    JS_IE -- "Consumer: producer-worker-inventory" --> PROD
    PROD -- "Publish: started / completed / failed" --> JS_PE

    CAT --> DB_CAT
    ORD --> DB_ORD
    INV --> DB_INV

    YAML -. "AdditionalFiles" .-> GEN
    GEN -. "Generates NatsContract" .-> ORD
    GEN -. "Generates NatsContract" .-> INV
    GEN -. "Generates NatsContract" .-> PROD
    GEN -. "Generates NatsContract" .-> GW
    YAML -. "yamldecode()" .-> TF
    TF -. "Provisioning" .-> Messaging
```

---

## Order Lifecycle & Execution Flow

Every order transitions through a deterministic state machine. For each unit of a product in an order line item, a dedicated production task (`ProducerTaskV1`) is dispatched, enabling unit-level real-time progress tracking.

### Order State Machine (`OrderStatus`)

```mermaid
stateDiagram-v2
    [*] --> Created : Order submitted
    Created --> WaitingForReserve : BOM snapshots captured (SHA-256)
    WaitingForReserve --> Reserved : Materials reserved in Inventory
    WaitingForReserve --> Error : Insufficient stock / reservation failure
    Reserved --> InProgress : Producer starts first task
    Reserved --> Error : Production task failure
    InProgress --> InProgress : Unit task completed (progress % updated)
    InProgress --> Done : All tasks completed (100%)
    InProgress --> Error : Material grab or production failure
```

### End-to-End Sequence Diagram

```mermaid
sequenceDiagram
    autonumber
    actor User as Operator (Web.Client)
    participant GW as Gateway.Api (GraphQL)
    participant ORD as Orders.Api
    participant CAT as Catalog.Api
    participant NATS as NATS JetStream
    participant INV as Inventory.Worker
    participant PROD as Producer.Worker

    User->>GW: mutation createOrder(items)
    GW->>ORD: POST /api/orders
    ORD->>CAT: GET /api/catalog/bom/{productId}
    CAT-->>ORD: BillOfMaterial (revision & materials)
    Note over ORD: Persist deterministic BomSnapshot (SHA-256)<br/>Order status: Created -> WaitingForReserve

    loop For each required material
        ORD->>NATS: Request manufacturing.inventory.tasks.reserve.v1
        NATS->>INV: Deliver reservation command
        INV-->>ORD: Reply InventoryReserveReplyV1 (Success, ReservationId)
    end

    Note over ORD: Order status: Reserved
    loop For each product unit (Quantity)
        ORD->>NATS: Publish manufacturing.producer.tasks.start.v1 (TaskId)
    end
    ORD-->>GW: 201 Created (Order)
    GW-->>User: OrderGql (Status: RESERVED, 0%)

    loop Execution of each ProducerTaskV1
        NATS->>PROD: Consume producer.tasks.start.v1
        PROD->>NATS: Publish producer.events.started.v1
        NATS-->>ORD: OrderProgressTracker -> Status: InProgress
        NATS-->>GW: NatsSubscriptionBridge -> WebSocket push (orderUpdated)
        GW-->>User: Live status & telemetry stream update

        PROD->>NATS: Publish inventory.tasks.grab.v1
        NATS->>INV: Consume reserved material stock
        INV->>NATS: Publish inventory.events.delivered.v1
        NATS-->>PROD: Confirm material delivery to shop floor

        Note over PROD: Manufacture product unit
        PROD->>NATS: Publish producer.events.completed.v1
        NATS-->>ORD: OrderProgressTracker -> Recalculate CompletedTasksCount / %
        NATS-->>GW: NatsSubscriptionBridge -> WebSocket push (orderUpdated)
        GW-->>User: Progress bar updated (until Status: DONE, 100%)
    end
```

---

## Repository Structure

```text
manufacturing-platform/
├── contracts/
│   └── nats.contract.yaml                  # Single source-of-truth NATS JetStream contract
├── deploy/
│   ├── nats/init-jetstream.sh              # JetStream bootstrap script for Docker Compose
│   ├── postgres/init.sql                   # Initializes Catalog, Orders, and Inventory databases
│   └── terraform/nats/                     # Syncs NATS contract with JetStream via yamldecode
├── src/
│   ├── Catalog.Api/                        # Catalog REST API (materials, products, BOM revisions)
│   ├── Catalog.Api.Client/                 # Strongly typed HTTP client for Catalog.Api
│   ├── Catalog.Core/                       # Catalog domain models
│   ├── Orders.Api/                         # Orders REST API & OrderProgressTracker background service
│   ├── Orders.Core/                        # Order domain models & deterministic BomSnapshot
│   ├── Inventory.Contracts/                # Inventory messaging contracts
│   ├── Inventory.DAL/                      # Inventory EF Core DbContext & migrations
│   ├── Inventory.Worker/                   # Background worker for stock reservations & material grabs
│   ├── Producer.Contracts/                 # Production task & event contracts
│   ├── Producer.Worker/                    # Background worker executing production tasks
│   ├── Manufacturing.Core/                 # Shared domain primitives (Product, Material, BillOfMaterial)
│   ├── Manufacturing.Nats.Contract/        # Host project for the source-generated NatsContract
│   ├── Gateway.Api/                        # GraphQL API Gateway (HotChocolate 16 + NATS WebSocket Bridge)
│   └── Web.Client/                         # Frontend (React 19, Vite, Tailwind CSS v4, Apollo Client)
├── tests/
│   ├── Catalog.Seed.Tests/                 # Catalog seeder & generator tests
│   └── Orders.Core.Tests/                  # Unit tests for BomSnapshot hashing & order progress lifecycle
├── tools/
│   ├── Catalog.Seed/                       # CLI tool for seeding catalog data
│   └── Manufacturing.Nats.Contracts.Generator/ # Roslyn incremental source generator + test suite
├── docker-compose.yml                      # Full-stack Docker Compose setup
└── ManufacturingPlatform.slnx              # Main solution file (.NET + .esproj)
```

---

## Quick Start

### Option 1: Full Stack with Docker Compose (Recommended)

Starts PostgreSQL, NATS JetStream, automatic stream/consumer initialization, EF Core startup migrations, all microservices, background workers, the GraphQL Gateway, and the React frontend:

```bash
docker compose up --build
```

Once started, services are available at:
- **Web UI (Control Center)**: [http://localhost:5173](http://localhost:5173)
- **GraphQL Gateway & Nitro IDE**: [http://localhost:5150/graphql](http://localhost:5150/graphql)
- **Catalog REST API**: `http://localhost:5142`
- **Orders REST API**: `http://localhost:5139`
- **NATS Monitoring**: `http://localhost:8222`

### Option 2: Local Development (IDE or CLI)

1. Start the infrastructure services (PostgreSQL, NATS, and JetStream bootstrap):
   ```bash
   docker compose up -d postgres nats nats-init
   ```
   *(Alternatively, provision NATS via Terraform in `deploy/terraform/nats`: `terraform init && terraform apply`).*

2. Build the solution and run unit tests:
   ```bash
   dotnet build ManufacturingPlatform.slnx
   dotnet test ManufacturingPlatform.slnx --filter "FullyQualifiedName!~PostgresIntegrationTests"
   ```

3. Run the frontend dev server (proxies `/graphql` HTTP and WebSocket traffic to `http://localhost:5150`):
   ```bash
   cd src/Web.Client
   npm install
   npm run dev
   ```
