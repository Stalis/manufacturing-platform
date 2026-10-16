# Catalog seed data

`catalog_seed.json` defines 557 materials and 12 synthetic products. The .NET tool
expands the definitions and reconciles them through the OpenAPI-generated
`CatalogClient`. It uses `Manufacturing.Core.MeasurementUnit` for named units.

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
- Each product specifies one positive `revision` (default `1`, up to the generated
  client's `int` maximum). The tool creates or reconciles only that revision.
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
uniqueness-conflict responses). Regenerate the contract/client after API changes:

```sh
bash scripts/catalog-api-client.sh
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
CATALOG_SEED_TEST_CONNECTION='Host=localhost;Port=55432;Database=postgres;Username=postgres;Password=test' \
  dotnet test tests/Catalog.Seed.Tests
```

This test creates a uniquely named `seed_test_*` database and drops it afterward.
It checks migration rejection of duplicates, preservation of legacy BOM data,
full seeding, reruns, conflict responses, and isolation between BOM revisions.
Without that environment variable, the integration test is explicitly skipped.
