# NATS Terraform provisioning

Terraform in `deploy/terraform/nats` is the only topology provisioner. It reads
`contracts/nats.contract.yaml`; there is no shell bootstrap or init container.
Start a reachable JetStream server before planning/applying, and apply successfully
before starting workers/APIs. For local Compose, start `postgres` and `nats` first.

From the repository root, select an environment and provision it:

```sh
export DEPLOY_ENV=local
export TF_DATA_DIR="$PWD/deploy/terraform/nats/.terraform/$DEPLOY_ENV"
terraform -chdir=deploy/terraform/nats init \
  -backend-config="path=state/$DEPLOY_ENV.tfstate"
terraform -chdir=deploy/terraform/nats plan \
  -var-file="environments/$DEPLOY_ENV.tfvars" \
  -out="$TF_DATA_DIR/provision.tfplan"
terraform -chdir=deploy/terraform/nats apply "$TF_DATA_DIR/provision.tfplan"
```

`environments/local.tfvars` contains the working localhost configuration.
For another environment, create a private `environments/<environment>.tfvars`
with `nats_servers` (comma-separated URLs) and, if needed, `stream_replicas`.
Change `DEPLOY_ENV` before running **all three commands**; every environment has
its own state file and backend/plugin metadata directory. A state must own only
one NATS account/cluster; do not repoint it at another server.

Credentials stay outside the repository:

```sh
export TF_VAR_nats_credentials="$HOME/.config/nats/deployment.creds"
```

The variable is an optional path to a NATS credentials file. In CI, inputs can
instead come entirely from `TF_VAR_nats_servers`, `TF_VAR_nats_credentials` and
`TF_VAR_stream_replicas`; omit `-var-file` from `plan` in that case. An explicit
`.tfvars` file takes precedence over `TF_VAR_*`. Never commit credentials, private
environment files, state or saved plans. Plans can contain sensitive data.

**Existing state:** back up `deploy/terraform/nats/terraform.tfstate` and identify
which environment it manages. For that environment's first initialization, replace
`init` above with `init -migrate-state` and approve the migration to its new path.
Do not use a new empty state against infrastructure already owned by the old state.
Resources created previously by the removed shell bootstrap must be imported into
the selected state before applying, rather than provisioned from a second owner.
Review the plan, particularly the Core-only reservation subject transition.

The checked-in backend is local, with single-host state locking. Before shared
team/CI deployments, configure an access-controlled remote backend with locking.
Backend settings are supplied at `init`, not through input variables; no remote
storage location or credentials are assumed by this repository.

Compose only orders process startup; it does not run Terraform. After recreating
a broker, run provisioning again before applications. State does not back up NATS
message storage. The current local NATS container has no persistent data volume;
do not remove/recreate it if its messages must be retained.

Validation without contacting a real NATS server (after initialization):

```sh
terraform -chdir=deploy/terraform/nats fmt -check -recursive
terraform -chdir=deploy/terraform/nats validate
terraform -chdir=deploy/terraform/nats test
```

# Typed NATS API clients

`contracts/nats.contract.yaml` is the source of truth for subject-to-model bindings.
Each `subjects` entry declares its literal `subject` and fully qualified C# `model`
together. Entries under `services.*.streams.*.subjects` use JetStream; entries
under `services.*.core_subjects` use Core NATS. Transport follows placement, so
there is no separate binding table or repeated `transport` field.
Core request/reply entries also declare `reply_model` and `queue_group`.
The entry key controls the generated method name; its service container controls
client ownership, independently of the subject text.
Terraform reads `subject.subject` from each stream entry and ignores DTO metadata
and Core subjects.

```yaml
services:
  producer:
    streams:
      commands:
        # Stream name, retention and consumers omitted from this example.
        subjects:
          start_v1:
            subject: producer.tasks.start.v1
            model: Producer.Contracts.Models.ProducerTaskV1
  inventory:
    core_subjects:
      reserve_v1:
        subject: inventory.tasks.reserve.v1
        model: Inventory.Contracts.Models.InventoryReserveTaskV1
        reply_model: Inventory.Contracts.Models.InventoryReserveReplyV1
        queue_group: inventory-worker
```

The existing Roslyn generator emits clients into `Manufacturing.Nats.Contract`
during the normal .NET build. Do not write client methods or subject/type mappings
in application code:

```csharp
var producer = new ProducerNatsClient(connection);
var inventory = new InventoryNatsClient(connection);

await producer.PublishStartV1Async(task, cancellationToken);
var reply = await inventory.RequestReserveV1Async(reserve, cancellationToken);

await foreach (var message in producer.ReadProducerWorkerAsync(cancellationToken))
{
    // message.Data is ProducerTaskV1; application processing controls ACK/NAK.
    await message.AckAsync(cancellationToken: cancellationToken);
}
```

JetStream publication waits for the server's acknowledgement and checks it.
Core publication does not claim persistence. Core request/reply uses a generated
typed responder; `InventoryNatsClient.RespondReserveV1Async` accepts the reserve
handler and error logger and uses the `inventory-worker` queue group.
Connection configuration still supplies the existing System.Text.Json serializer
contexts for the DTOs each application sends and receives.

To add a subject, declare its `subject` and `model` together in the appropriate
service/stream and update JetStream consumer filters if needed. A closed generic
DTO can be used as a subject's model.
Each durable consumer must match one payload type; split filters into separate
consumers when payload types differ. Duplicate subjects, missing models,
JetStream reply metadata, and heterogeneous consumer payloads fail the build.
The C# compiler checks that the bound type exists and rejects a wrong model passed
to a generated method. No analyzer is installed: direct raw NATS calls remain
technically possible and should not be used by application code.

Reservation rollout: `inventory.tasks.reserve.v1` is now Core-only. Update the
Terraform topology and deploy the Inventory responder with Orders using the new
client. Coordinate the switch without in-flight reservation requests; inspect
and reconcile old queued reserve commands before removing that stream subject.
JetStream ACK reply subjects must never be used for application responses.

`inventory.tasks.release.v1` (Core request/reply, same queue group) returns the
unconsumed quantities of a reservation to stock and marks it `Canceled`; repeats
are no-ops. A release that precedes its reserve creates a canceled placeholder
and replies `NotFound`; late reserves cannot revive it. Reserve, grab and release
lock the reservation before stock rows; concurrent releases cannot return stock twice.
Orders cleans up before looking up material labels and finishes task persistence
and publication independently of caller cancellation. On publication failure it
releases reservations with no attempted task; possibly delivered tasks retain
their stock and require reconciliation. Deploy Inventory before Orders.

Build and regression checks:

```sh
dotnet build ManufacturingPlatform.slnx
dotnet test tools/Manufacturing.Nats.Contracts.Generator/Manufacturing.Nats.Contracts.Generator.Tests
```

# Contract code generation

TypeSpec files in `contracts/` are the source of truth for generated C#. Do not
edit generated files by hand:

| Contract | Generated |
|---|---|
| `producer-events.tsp` | `src/Producer.Contracts/Generated/Models.g.cs` |
| `catalog.tsp` | `src/Catalog.Contracts/Generated/*.g.cs` (DTOs, `CatalogClient`), `src/Catalog.Api/Generated/CatalogApi.g.cs` (handlers, `MapCatalogApi`) |
| `orders.tsp` | `src/Orders.Contracts/Generated/*.g.cs` (DTOs, `OrdersClient`), `src/Orders.Api/Generated/OrdersApi.g.cs` (handlers, `MapOrdersApi`) |

Other NATS message DTOs are hand-written; `contracts/nats.contract.yaml` binds
subjects to them and Terraform consumes only its topology.

Node.js 22 or newer is needed for generation, not for normal .NET builds or runtime:

```sh
npm --prefix tools/Contracts.Generator ci --ignore-scripts
npm --prefix tools/Contracts.Generator run generate
npm --prefix tools/Contracts.Generator run check
npm --prefix tools/Contracts.Generator test
```

The wrapper invokes the pinned TypeSpec compiler, `@typespec/http` and our emitter
in-process. Compiler diagnostics fail the command. Outputs replace their
destinations only after every input compiles; `check` detects stale C# without
changing it. CI should run `ci`, `check`, the wrapper tests, and the normal .NET
build. Generated C# is committed so consumers do not need Node.js to build.
Default inputs and output paths live in `tools/Contracts.Generator/targets.json`.
`generate` also writes `src/Catalog.Api/openapi.json` and `src/Orders.Api/openapi.json`
with `@typespec/openapi3`; they are git-ignored and served at `/openapi/v1.json`
in Development (404 with a hint when not generated).

For other inputs, pass each `.tsp` followed by its `kind=path` outputs; kinds are
`models`, `client`, `server` and `openapi`:

```sh
node tools/Contracts.Generator/generate.mjs input.tsp models=Models.g.cs client=Client.g.cs [--check]
```

## Models

The emitter supports namespaced positional records, including generic records
and nested closed generic field types, annotated with `@csharpRecord(true)`
(sealed) or `@csharpRecord(false)`; arrays (`T[]`); `T | null` (C# `T?`, the
property stays required); primitive string/boolean/int32/uint32/int64/uint64/
float64/decimal fields; and UUID string scalars mapped to `System.Guid`
(`Manufacturing.CSharp.Guid`, shared by HTTP and message contracts). The TypeSpec property name is the
JSON name: a camelCase name becomes a PascalCase C# property with
`[JsonPropertyName]`. Enums are either all explicit int32 values (numeric on the
wire, as in NATS messages) or all value-less (string on the wire; integer values
are rejected by `StrictStringEnumConverter<TEnum>`). Type parameters become C# type parameters; template
instances become references to the corresponding generic record. `@doc` on
declarations and operations becomes XML documentation.

Optional fields, field defaults, other unions, inheritance, generic parameter
constraints and generic operations fail generation rather than silently changing
their semantics. Conflicting record/property/type-parameter names also fail
without overwriting existing output. Define unsupported constructs' C# and JSON
behavior before adding support. Generator changes must preserve wire format,
constructor parameters and enum values; compiling DTOs alone does not establish
message compatibility. Register closed NATS message types in each service's
System.Text.Json source-generation context.

## HTTP APIs

An HTTP contract imports `tools/Contracts.Generator/http.tsp` (it re-exports
`@typespec/http`) and declares one `@service` namespace whose operations live in
interfaces. Operation names must be unique per service. For each interface
`X` the server output declares `IXHandler` in `{Service}.Api`, one method per operation returning
ASP.NET Core typed results (`Results<Ok<T>, NotFound<ProblemDetails>, …>`), and
`Map{Service}Api()` binds every route to it and serves OpenAPI only in Development.
`Add{Service}Api()` registers problem details and the strict JSON options below;
the API registers handler implementations in DI. DTOs and the client remain in
`{Service}.Contracts`. The client output is one `{Service}Client` (an
`HttpClient` typed client) with the same operations; non-success responses
throw `HttpRequestException` whose `StatusCode` is set and whose message holds
the problem body.

Supported: required path parameters, one explicit `@body` of application/json,
exact status codes 200/201/202/204/400/404/409/422 with at most one body, and
errors through `Manufacturing.Http.BadRequest`/`NotFound`/`Conflict`
(RFC 9457, `application/problem+json`). Query/header/cookie parameters,
response headers, other status codes and successes with different body types
fail generation. Each API sets `RespectNullableAnnotations` and
`RespectRequiredConstructorParameters`, so bodies missing a required property are
rejected with 400.

The generator tests require the .NET 11 SDK: they compile generated generic
records, and run a generated server and client against each other over HTTP.

# Catalog seed data

`catalog_seed.json` defines 557 materials and 12 synthetic products. The .NET tool
expands the definitions and reconciles them through the generated
`CatalogClient`. It uses the contract's `Catalog.Contracts.MeasurementUnit` for named units.

Run from the repository root:

```sh
# Validate definitions and preview remote changes, with no writes.
dotnet run --project tools/Catalog.Seed -- --dry-run

# Create/update materials, products, and the specified BOM revision.
dotnet run --project tools/Catalog.Seed

# Reconcile products/BOMs without creating or changing materials.
dotnet run --project tools/Catalog.Seed -- --products-only

# Combine options or select another API/data file.
dotnet run --project tools/Catalog.Seed -- --products-only --dry-run \
  --base-url http://localhost:5142 --data scripts/catalog_seed.json
```

`--base-url` is the API origin, without `/api/catalog`. The default is
`http://localhost:5142`. The default JSON is copied beside the executable so a
built/published tool works from any working directory. `--dry-run` still requires
a running API to compare the desired dataset with existing rows.

## Data rules

- Unit values are enum names (`Each`, `Meter`, `SquareCentimeter`), never numeric IDs.
- Material and product SKUs are stable, case-sensitive keys. Matching records keep
  their IDs. Duplicate SKUs are errors, including duplicates already in the API.
- Each BOM line has `sku` and a positive integer `quantity`, measured in that
  material's unit. For example, `PCB-2L` quantity `80` means **80 cm²**, not 80 boards;
  a wire quantity `2` means **2 meters**. These are manufacturing requirements,
  not inventory balances.
- Each product specifies one positive `revision` (default `1`, up to the contract's
  `uint32` maximum). The tool creates or reconciles only that revision.
  Other revisions are left alone. Increment the value to seed a new revision;
  changes with the same value update the existing revision in place.
- All product references must exist in the generated material dataset. In
  products-only mode, they must also exist remotely with matching units. Missing
  data fails before writes; recipes are never silently changed.
- Rerunning an identical dataset sends no writes. Changed seed-defined names,
  units, and target BOMs are updated. Unlisted catalog records are left alone.
- API calls are not one transaction. A failed run may have completed earlier
  requests; rerun to reconcile them. POSTs are not automatically retried. Do not
  run competing seeders or edit the same products concurrently: uniqueness
  conflicts stop the run, but updates have no optimistic concurrency token.

The data is synthetic. Product recipes illustrate catalog relationships, not
validated electrical designs. The former Python seeders have been replaced by
this single tool.

## API and database prerequisites

Use the updated Catalog API (product listing, revision-number BOM lookup, and
uniqueness-conflict responses). Regenerate the contract/client after changing
`contracts/catalog.tsp`:

```sh
npm --prefix tools/Contracts.Generator run generate
```

Building or running the seeder does **not** apply migrations. Deploy migrations
separately using a migration-capable database account, then start the updated API.
API accounts remain CRUD-only; the revision table needs the same CRUD grants as
other application tables (owner default privileges may already provide them).

The `CatalogUniqueKeys` migration reports duplicate material/product SKUs or BOM
pairs and aborts, without merging/deleting records. The revision migration now
backfills revision 1 for old BOM items when upgrading a populated database. That
backfill is not rerun on databases where the revision migration is already applied.
`AlignBomRevisionRelationships` moves product ownership to the revision and removes
the redundant item `ProductId` column/index. It rejects disagreeing item/revision
product IDs; orphan revisions also prevent its product foreign key being added.
Resolve reported inconsistencies before retrying migration. Downgrading to an
unversioned BOM is blocked when a product has multiple revisions.

## Tests

```sh
dotnet test tests/Catalog.Seed.Tests
```

The HTTP tests use the real generated client with an in-memory HTTP handler.
They check validation, formatting, reruns, dry runs, products-only mode, and
recovery after BOM failure.

For the PostgreSQL/API integration test, point `CATALOG_SEED_TEST_CONNECTION` at a
**disposable PostgreSQL server**, using an account with `CREATEDB` permission:

```sh
CATALOG_SEED_TEST_CONNECTION='Host=localhost;Port=15432;Database=postgres;Username=postgres;Password=postgres' \
  dotnet test tests/Catalog.Seed.Tests
```

This test creates a uniquely named `seed_test_*` database and drops it afterward.
It checks migration rejection of duplicates, preservation of legacy BOM data,
full seeding, reruns, conflict responses, and isolation between BOM revisions.
Without that environment variable, the integration test is explicitly skipped.
