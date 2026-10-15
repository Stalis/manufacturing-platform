#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/.."

# 1. Build Catalog and generate its OpenAPI
dotnet build src/Catalog.Api

# 2. Update contract snapshot
mkdir -p contracts

cp \
  src/Catalog.Api/obj/Catalog.Api.json \
  contracts/catalog.openapi.json

# 3. Generate client
mkdir -p src/Catalog.Api.Client/Generated

dotnet nswag openapi2csclient \
  /input:contracts/catalog.openapi.json \
  /output:src/Catalog.Api.Client/Generated/CatalogClient.cs \
  /classname:CatalogClient \
  /namespace:Manufacturing.Catalog.Client \
  /GenerateClientInterfaces:true \
  /OperationGenerationMode:SingleClientFromOperationId \
  /InjectHttpClient:true \
  /DisposeHttpClient:false \
  /UseBaseUrl:false

# 4. Verify generated client
dotnet build src/Catalog.Api.Client