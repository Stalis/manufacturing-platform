import "../tools/Contracts.Generator/http.tsp";

using Http;
using Manufacturing.CSharp;
using Manufacturing.Http;

/** Materials, products and their bill-of-material revisions. */
@service(#{ title: "Catalog API" })
namespace Catalog.Contracts;

enum MeasurementUnit {
  Each,
  Kilogram,
  Gram,
  Liter,
  Milliliter,
  Meter,
  Centimeter,
  Millimeter,
  SquareMeter,
  SquareCentimeter,
  SquareMillimeter,
  Inch,
  Foot,
  SquareFoot,
}

@csharpRecord(true)
model Material {
  id: Guid;
  sku: string;
  name: string;
  measurementUnit: MeasurementUnit;
}

/** Material fields set on create and full replacement. SKU is unique. */
@csharpRecord(true)
model MaterialInput {
  sku: string;
  name: string;
  measurementUnit: MeasurementUnit;
}

@csharpRecord(true)
model Product {
  id: Guid;
  sku: string;
  name: string;
}

/** Product fields set on create and full replacement. SKU is unique. */
@csharpRecord(true)
model ProductInput {
  sku: string;
  name: string;
}

/** One material line; quantity must be positive and a material occurs once per revision. */
@csharpRecord(true)
model BillOfMaterialItem {
  materialId: Guid;
  quantity: int32;
}

/** A positive, per-product revision number and its material lines. */
@csharpRecord(true)
model BillOfMaterial {
  productId: Guid;
  revision: uint32;
  materials: BillOfMaterialItem[];
}

@route("/api/catalog/materials")
interface Materials {
  @get listMaterials(): Material[];
  @get getMaterial(@path id: Guid): Material | NotFound;
  @post createMaterial(@body material: MaterialInput): (CreatedResponse & {
    @body material: Material;
  }) | BadRequest | Conflict;
  @put replaceMaterial(@path id: Guid, @body material: MaterialInput): NoContentResponse | BadRequest | NotFound | Conflict;

  /** Fails with 409 while a bill of material references the material. */
  @delete deleteMaterial(@path id: Guid): NoContentResponse | NotFound | Conflict;
}

@route("/api/catalog/products")
interface Products {
  @get listProducts(): Product[];
  @get getProduct(@path id: Guid): Product | NotFound;
  @post createProduct(@body product: ProductInput): (CreatedResponse & {
    @body product: Product;
  }) | BadRequest | Conflict;
  @put replaceProduct(@path id: Guid, @body product: ProductInput): NoContentResponse | BadRequest | NotFound | Conflict;

  /** Also deletes the product's bill-of-material revisions. */
  @delete deleteProduct(@path id: Guid): NoContentResponse | NotFound;
}

@route("/api/catalog/bill-of-material")
interface BillsOfMaterial {
  @get
  @route("/{productId}/revisions/{revision}")
  getBillOfMaterial(@path productId: Guid, @path revision: uint32): BillOfMaterial | NotFound;

  @get
  @route("/{productId}/{revisionId}")
  getBillOfMaterialByRevisionId(@path productId: Guid, @path revisionId: Guid): BillOfMaterial | NotFound;

  @post createBillOfMaterial(@body billOfMaterial: BillOfMaterial): (CreatedResponse & {
    @body billOfMaterial: BillOfMaterial;
  }) | BadRequest | Conflict;

  /** Replaces the material lines of an existing product revision. */
  @put replaceBillOfMaterial(@body billOfMaterial: BillOfMaterial): NoContentResponse | BadRequest | NotFound | Conflict;

  @delete
  @route("/{productId}/{revisionId}")
  deleteBillOfMaterial(@path productId: Guid, @path revisionId: Guid): NoContentResponse | NotFound;

  @post
  @route("/{productId}/{revisionId}/items")
  addBillOfMaterialItem(
    @path productId: Guid,
    @path revisionId: Guid,
    @body item: BillOfMaterialItem,
  ): CreatedResponse | BadRequest | NotFound | Conflict;

  @put
  @route("/{productId}/{revisionId}/items")
  updateBillOfMaterialItem(
    @path productId: Guid,
    @path revisionId: Guid,
    @body item: BillOfMaterialItem,
  ): NoContentResponse | BadRequest | NotFound;

  @delete
  @route("/{productId}/{revisionId}/items/{materialId}")
  removeBillOfMaterialItem(
    @path productId: Guid,
    @path revisionId: Guid,
    @path materialId: Guid,
  ): NoContentResponse | NotFound;
}
