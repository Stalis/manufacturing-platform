# Manufacturing Platform

Распределённая событийно-ориентированная платформа управления производством на базе **.NET 10**, **NATS JetStream**, **PostgreSQL**, **HotChocolate GraphQL (BFF)** и **React 19**.

---

## Архитектура системы

Платформа построена вокруг подхода **Contract-First** для обмена сообщениями в NATS JetStream и паттерна **Backend-for-Frontend (GraphQL API Gateway)** для клиентского приложения:

- Единый контракт [contracts/nats.contract.yaml](contracts/nats.contract.yaml) используется одновременно:
  1. **Roslyn Incremental Source Generator** (`Manufacturing.Nats.Contracts.Generator`) — на этапе компиляции генерирует строго типизированный C#-класс `NatsContract` со всеми стримами, субъектами и консьюмерами.
  2. **Terraform** ([deploy/terraform/nats](deploy/terraform/nats)) — через встроенную функцию `yamldecode` автоматически разворачивает стримы и durable-консьюмеры в NATS JetStream.

```mermaid
flowchart TB
    subgraph Client["Клиентский уровень"]
        UI["Web.Client (React 19 + Vite + Apollo)"]
    end

    subgraph BFF["API Gateway (BFF)"]
        GW["Gateway.Api (.NET 10 + HotChocolate GraphQL)"]
    end

    subgraph Services["Микросервисы и воркеры (.NET 10)"]
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
    GEN -. "Генерация NatsContract" .-> ORD
    GEN -. "Генерация NatsContract" .-> INV
    GEN -. "Генерация NatsContract" .-> PROD
    GEN -. "Генерация NatsContract" .-> GW
    YAML -. "yamldecode()" .-> TF
    TF -. "Provisioning" .-> Messaging
```

---

## Жизненный цикл заказа и последовательность выполнения

Каждый заказ проходит через детерминированную цепочку состояний. Для каждой единицы продукции в позиции заказа создаётся отдельная производственная задача (`ProducerTaskV1`), что позволяет отслеживать прогресс выполнения заказа с точностью до штуки в реальном времени.

### Диаграмма состояний заказа (`OrderStatus`)

```mermaid
stateDiagram-v2
    [*] --> Created : Создание заказа
    Created --> WaitingForReserve : Снятие BOM-снимков (SHA-256)
    WaitingForReserve --> Reserved : Материалы зарезервированы в Inventory
    WaitingForReserve --> Error : Нехватка материалов / ошибка резерва
    Reserved --> InProgress : Producer начал выполнение первой задачи
    Reserved --> Error : Ошибка задачи производства
    InProgress --> InProgress : Завершение очередного таска (обновление %)
    InProgress --> Done : Все задачи успешно завершены (100%)
    InProgress --> Error : Сбой захвата материалов или задачи
```

### Диаграмма последовательности (End-to-End Flow)

```mermaid
sequenceDiagram
    autonumber
    actor User as Оператор (Web.Client)
    participant GW as Gateway.Api (GraphQL)
    participant ORD as Orders.Api
    participant CAT as Catalog.Api
    participant NATS as NATS JetStream
    participant INV as Inventory.Worker
    participant PROD as Producer.Worker

    User->>GW: mutation createOrder(items)
    GW->>ORD: POST /api/orders
    ORD->>CAT: GET /api/catalog/bom/{productId}
    CAT-->>ORD: BillOfMaterial (ревизия и материалы)
    Note over ORD: Сохранение детерминированного BomSnapshot (SHA-256)<br/>Статус заказа: Created -> WaitingForReserve

    loop Для каждого требуемого материала
        ORD->>NATS: Request manufacturing.inventory.tasks.reserve.v1
        NATS->>INV: Доставка команды резервирования
        INV-->>ORD: Reply InventoryReserveReplyV1 (Success, ReservationId)
    end

    Note over ORD: Статус заказа: Reserved
    loop Для каждой единицы товара (Quantity)
        ORD->>NATS: Publish manufacturing.producer.tasks.start.v1 (TaskId)
    end
    ORD-->>GW: 201 Created (Order)
    GW-->>User: OrderGql (Status: RESERVED, 0%)

    loop Выполнение каждой задачи ProducerTaskV1
        NATS->>PROD: Consume producer.tasks.start.v1
        PROD->>NATS: Publish producer.events.started.v1
        NATS-->>ORD: OrderProgressTracker -> Статус: InProgress
        NATS-->>GW: NatsSubscriptionBridge -> WebSocket push (orderUpdated)
        GW-->>User: Live-обновление статуса и ленты событий

        PROD->>NATS: Publish inventory.tasks.grab.v1
        NATS->>INV: Списание зарезервированного материала
        INV->>NATS: Publish inventory.events.delivered.v1
        NATS-->>PROD: Подтверждение доставки материалов в цех

        Note over PROD: Производство единицы продукции
        PROD->>NATS: Publish producer.events.completed.v1
        NATS-->>ORD: OrderProgressTracker -> Пересчёт CompletedTasksCount / %
        NATS-->>GW: NatsSubscriptionBridge -> WebSocket push (orderUpdated)
        GW-->>User: Прогресс-бар обновлён (вплоть до Status: DONE, 100%)
    end
```

---

## Структура репозитория

```text
manufacturing-platform/
├── contracts/
│   └── nats.contract.yaml                  # Единый контракт стримов, субъектов и консьюмеров NATS JetStream
├── deploy/
│   ├── nats/init-jetstream.sh              # Инициализация JetStream для Docker Compose
│   ├── postgres/init.sql                   # Инициализация БД Catalog, Orders и Inventory
│   └── terraform/nats/                     # Синхронизация контракта NATS с JetStream через yamldecode
├── src/
│   ├── Catalog.Api/                        # REST API каталога (материалы, продукты, ревизии BOM)
│   ├── Catalog.Api.Client/                 # Типизированный HTTP-клиент для Catalog.Api
│   ├── Catalog.Core/                       # Доменные модели каталога
│   ├── Orders.Api/                         # REST API заказов и фоновый трекер прогресса OrderProgressTracker
│   ├── Orders.Core/                        # Доменные модели заказов и детерминированный BomSnapshot
│   ├── Inventory.Contracts/                # Контракты сообщений склада
│   ├── Inventory.DAL/                      # EF Core контекст и миграции склада
│   ├── Inventory.Worker/                   # Воркер резервирования и отгрузки материалов со склада
│   ├── Producer.Contracts/                 # Контракты производственных задач и событий
│   ├── Producer.Worker/                    # Воркер выполнения производственных задач
│   ├── Manufacturing.Core/                 # Общие модели (Product, Material, BillOfMaterial)
│   ├── Manufacturing.Nats.Contract/        # Проект-носитель сгенерированного NatsContract
│   ├── Gateway.Api/                        # GraphQL API Gateway (HotChocolate 16 + NATS WebSocket Bridge)
│   └── Web.Client/                         # Фронтенд (React 19, Vite, Tailwind CSS v4, Apollo Client)
├── tests/
│   ├── Catalog.Seed.Tests/                 # Тесты генератора и сидера каталога
│   └── Orders.Core.Tests/                  # Юнит-тесты BomSnapshot и расчёта прогресса/статусов заказа
├── tools/
│   ├── Catalog.Seed/                       # CLI-утилита наполнения каталога тестовыми данными
│   └── Manufacturing.Nats.Contracts.Generator/ # Инкрементальный генератор Roslyn для NATS-контракта + тесты
├── docker-compose.yml                      # Полный стек для запуска одной командой
└── ManufacturingPlatform.slnx              # Основной файл решения (.NET + .esproj)
```

---

## Быстрый старт

### Вариант 1: Полный стек в Docker Compose (рекомендуется)

Запускает PostgreSQL, NATS JetStream, автоматическую инициализацию стримов/консьюмеров, накатку миграций EF Core, все микросервисы, воркеры, GraphQL Gateway и фронтенд:

```bash
docker compose up --build
```

После запуска сервисы доступны по адресам:
- **Web UI (Панель управления)**: [http://localhost:5173](http://localhost:5173)
- **GraphQL Gateway & Nitro IDE**: [http://localhost:5150/graphql](http://localhost:5150/graphql)
- **Catalog REST API**: `http://localhost:5142`
- **Orders REST API**: `http://localhost:5139`
- **NATS Monitoring**: `http://localhost:8222`

### Вариант 2: Локальная разработка (из IDE или CLI)

1. Поднимите инфраструктуру (PostgreSQL, NATS и инициализацию стримов):
   ```bash
   docker compose up -d postgres nats nats-init
   ```
   *(Либо примените конфигурацию через Terraform в `deploy/terraform/nats`: `terraform init && terraform apply`).*

2. Соберите решение и запустите тесты:
   ```bash
   dotnet build ManufacturingPlatform.slnx
   dotnet test ManufacturingPlatform.slnx --filter "FullyQualifiedName!~PostgresIntegrationTests"
   ```

3. Запустите фронтенд в режиме разработки (с проксированием `/graphql` и WebSocket на `http://localhost:5150`):
   ```bash
   cd src/Web.Client
   npm install
   npm run dev
   ```
