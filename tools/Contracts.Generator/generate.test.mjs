import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import test from "node:test";

const wrapper = fileURLToPath(new URL("./generate.mjs", import.meta.url));
const library = (name) => JSON.stringify(fileURLToPath(new URL(name, import.meta.url)).replaceAll("\\", "/"));
const header = `import ${library("./decorators.tsp")};\nusing Manufacturing.CSharp;\nnamespace Example;\n`;
const httpHeader = `import ${library("./http.tsp")};\nusing Http;\nusing Manufacturing.CSharp;\nusing Manufacturing.Http;\n` +
  "@service namespace Example;\n";
function run(input, outputs, ...args) {
  const result = spawnSync(process.execPath, [wrapper, input, ...outputs, ...args], { encoding: "utf8" });
  if (result.error) throw result.error;
  return result;
}
async function withDirectory(action) {
  const directory = await mkdtemp(join(tmpdir(), "contracts-test-"));
  try {
    await action(directory);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
}
function dotnetRun(project) {
  const result = spawnSync("dotnet", ["run", "--project", project, "--nologo"], { encoding: "utf8", timeout: 120000 });
  if (result.error) throw result.error;
  assert.equal(result.status, 0, result.stdout + result.stderr);
}

for (const [prefix, kind, declaration] of [
  [header, "models", "@csharpRecord(true) model Event { Name?: string; }"],
  [header, "models", "@csharpRecord(true) model Event { Name: string = \"default\"; }"],
  [header, "models", "@csharpRecord(true) model Event { Name: string | int32; }"],
  [header, "models", "enum Status { Value: \"text\" }"],
  [header, "models", "enum Status { Named, Numbered: 1 }"],
  [header, "models", "@csharpRecord(true) model Event { Event: string; }"],
  [header, "models", "@csharpRecord(true) model Event { name: string; Name: string; }"],
  [header, "models", "@csharpRecord(true) model Event { Clone: string; }"],
  [header, "models", "@csharpRecord(true) model Event<T> { T: string; }"],
  [header, "models", "@csharpRecord(true) model Event<Event> { Value: Event; }"],
  [header, "models", "@csharpRecord(true) model Event<T extends string> { Value: T; }"],
  [header, "models", "@csharpRecord(true) model Event<T extends valueof string> {}"],
  [header, "models", "@csharpRecord(true) model Event { Name: string; } op send<T>(payload: T): void;"],
  [header, "models", "@csharpRecord(true) model Event { Name: string; } model Missing<T> { Value: T; }"],
  [header, "models", "@csharpRecord(true) model Event {"],
  [httpHeader, "client", "op loose(): void;"],
  [httpHeader, "client", "@route(\"/x\") interface X { @get list(@query q: string): void; }"],
  [httpHeader, "client", "@route(\"/x\") interface X { @get get(): { @header etag: string; @body value: string }; }"],
  [httpHeader, "client", "@route(\"/x\") interface X { @get get(): int32 | string; }"],
  [httpHeader, "server", "@route(\"/x\") interface X { @get get(): { @statusCode code: 418; @body value: string }; }"],
  [httpHeader, "client", "interface A { @get @route(\"/a\") same(): void; } interface B { @get @route(\"/b\") same(): void; }"],
]) {
  test(`Failed compilation preserves existing output: ${declaration}`, () => withDirectory(async (directory) => {
    const input = join(directory, "main.tsp");
    const output = join(directory, "Output.cs");
    await writeFile(input, prefix + declaration);
    await writeFile(output, "existing output");
    const result = run(input, [`${kind}=${output}`]);
    assert.equal(result.status, 1, result.stdout + result.stderr);
    assert.equal(await readFile(output, "utf8"), "existing output");
  }));
}

test("Check accepts regenerated output and rejects drift without overwriting it", () => withDirectory(async (directory) => {
  const input = join(directory, "main.tsp");
  const output = join(directory, "Models.cs");
  await writeFile(input, header + "@csharpRecord(true) model Event { Name: string; }");
  const generated = run(input, [`models=${output}`]);
  assert.equal(generated.status, 0, generated.stdout + generated.stderr);
  const checked = run(input, [`models=${output}`], "--check");
  assert.equal(checked.status, 0, checked.stdout + checked.stderr);
  await writeFile(output, "stale output");
  assert.equal(run(input, [`models=${output}`], "--check").status, 1);
  assert.equal(await readFile(output, "utf8"), "stale output");
}));

test("Generic records compile and closed nested types preserve JSON data", () => withDirectory(async (directory) => {
  const input = join(directory, "main.tsp");
  await writeFile(input, header + `
@csharpRecord(false) model Cell<T> { Value: T; }
@csharpRecord(true) model Pair<A, B = int32> { First: Cell<A>; Second: B; }
@csharpRecord(true) model Unused<T> { Value: T; }
enum Status { Ready: 7 }
@csharpRecord(true) model Event { Data: Pair<string>; State: Cell<Status>; }
`);
  const generated = run(input, [`models=${join(directory, "Models.cs")}`]);
  assert.equal(generated.status, 0, generated.stdout + generated.stderr);
  await writeFile(join(directory, "Smoke.csproj"), `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net11.0</TargetFramework></PropertyGroup>
</Project>`);
  await writeFile(join(directory, "Program.cs"), `
using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Example;
var value = new Event(new Pair<string, int>(new Cell<string>("part"), 42), new Cell<Status>(Status.Ready));
var json = JsonSerializer.Serialize(value, Context.Default.Event);
using var document = JsonDocument.Parse(json);
var data = document.RootElement.GetProperty("Data");
if (data.GetProperty("First").GetProperty("Value").GetString() != "part" ||
    data.GetProperty("Second").GetInt32() != 42 ||
    document.RootElement.GetProperty("State").GetProperty("Value").GetInt32() != 7)
    throw new Exception("Generic field types or JSON shape changed.");
if (JsonSerializer.Deserialize(json, Context.Default.Event) != value)
    throw new Exception("Generic JSON round-trip lost data.");
if (!new Unused<bool>(true).Value) throw new Exception("Unused template was not emitted.");
Console.WriteLine("Generic JSON round-trip passed.");
[JsonSerializable(typeof(Event))]
internal partial class Context : JsonSerializerContext;
`);
  dotnetRun(join(directory, "Smoke.csproj"));
}));

test("Generated client and server agree on routes, JSON and problem responses", () => withDirectory(async (directory) => {
  const input = join(directory, "main.tsp");
  await writeFile(input, httpHeader + `
enum Kind { Small, Large }
@csharpRecord(true) model Item { id: Guid; name: string; kind: Kind; note: string | null; tags: string[]; }
@csharpRecord(true) model ItemInput { name: string; kind: Kind; note: string | null; tags: string[]; }
@route("/api/items")
interface Items {
  @get listItems(): Item[];
  @get getItem(@path id: Guid): Item | NotFound;
  @get @route("/by-rank/{rank}") getItemByRank(@path rank: uint32): Item | NotFound;
  @post createItem(@body item: ItemInput): (CreatedResponse & { @body item: Item }) | BadRequest;
  @delete deleteItem(@path id: Guid): NoContentResponse | NotFound;
}
`);
  const outputs = ["models", "client", "server"].map((kind) => `${kind}=${join(directory, `${kind}.cs`)}`);
  const generated = run(input, [...outputs, `openapi=${join(directory, "openapi.json")}`]);
  assert.equal(generated.status, 0, generated.stdout + generated.stderr);
  const openapi = JSON.parse(await readFile(join(directory, "openapi.json"), "utf8"));
  assert.ok(openapi.paths["/api/items/by-rank/{rank}"].get);
  await writeFile(join(directory, "Smoke.csproj"), `<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net11.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <NoWarn>CS1998</NoWarn>
  </PropertyGroup>
</Project>`);
  await writeFile(join(directory, "Program.cs"), `
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Example;
using Example.Api;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder();
builder.Logging.ClearProviders();
builder.Services.AddExampleApi();
builder.Services.AddSingleton<IItemsHandler, Handler>();
await using var app = builder.Build();
app.MapExampleApi();
app.Urls.Add("http://127.0.0.1:0");
await app.StartAsync();
using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single() + "/") };
var client = new ExampleClient(http);

var created = await client.CreateItemAsync(new ItemInput("first", Kind.Large, null, ["a", "b"]));
Check(created is { Name: "first", Kind: Kind.Large, Note: null } && created.Tags.SequenceEqual(["a", "b"]), "create");
Check((await client.GetItemAsync(created.Id)).Name == "first", "get");
Check((await client.GetItemByRankAsync(1)).Id == created.Id, "uint path");
Check((await client.ListItemsAsync()).Single().Id == created.Id, "list");
var json = await http.GetStringAsync($"api/items/{created.Id}");
Check(json.Contains("\\"kind\\":\\"Large\\"") && json.Contains("\\"note\\":null"), "wire format: " + json);

var missing = await http.GetAsync($"api/items/{Guid.NewGuid()}");
Check(missing.StatusCode == HttpStatusCode.NotFound &&
      missing.Content.Headers.ContentType?.MediaType == "application/problem+json", "problem response");
try
{
    await client.DeleteItemAsync(Guid.NewGuid());
    Check(false, "delete of a missing item must throw");
}
catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.NotFound && error.Message.Contains("No item"))
{
}
var incomplete = await http.PostAsync("api/items",
    new StringContent("""{"name":"x","kind":"Small","tags":[]}""", Encoding.UTF8, "application/json"));
Check(incomplete.StatusCode == HttpStatusCode.BadRequest, "missing required property");
foreach (var number in new[] { 0, 99 })
{
    using var numeric = await http.PostAsync("api/items", new StringContent(
        $$"""{"name":"x","kind":{{number}},"note":null,"tags":[]}""", Encoding.UTF8, "application/json"));
    Check(numeric.StatusCode == HttpStatusCode.BadRequest, "integer enum must be rejected");
}
Check(ExampleApiEndpoints.MapEnum<Kind>(Kind.Large) == Kind.Large, "enum mapping");
try
{
    ExampleApiEndpoints.MapEnum<Kind>((Kind)99);
    Check(false, "undefined enum must be rejected");
}
catch (InvalidOperationException) { }
var encoded = JsonSerializer.Serialize(created, Context.Default.Item);
Check(JsonSerializer.Deserialize(encoded, Context.Default.Item)?.Kind == Kind.Large, "source-generated enum round-trip");
try
{
    JsonSerializer.Deserialize(encoded.Replace("\\"Large\\"", "99"), Context.Default.Item);
    Check(false, "source-generated deserialization must reject integer enums");
}
catch (JsonException) { }
await client.DeleteItemAsync(created.Id);
Console.WriteLine("HTTP round-trip passed.");

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

sealed class Handler : IItemsHandler
{
    private readonly ConcurrentDictionary<Guid, Item> items = new();

    public Task<Ok<Item[]>> ListItemsAsync(CancellationToken cancellationToken) =>
        Task.FromResult(TypedResults.Ok(items.Values.ToArray()));

    public async Task<Results<Ok<Item>, NotFound<ProblemDetails>>> GetItemAsync(Guid id, CancellationToken cancellationToken) =>
        items.TryGetValue(id, out var item) ? TypedResults.Ok(item) : TypedResults.NotFound(Missing(id));

    public async Task<Results<Ok<Item>, NotFound<ProblemDetails>>> GetItemByRankAsync(uint rank, CancellationToken cancellationToken) =>
        items.Values.ElementAtOrDefault((int)rank - 1) is { } item ? TypedResults.Ok(item) : TypedResults.NotFound(Missing(Guid.Empty));

    public async Task<Results<Created<Item>, BadRequest<ProblemDetails>>> CreateItemAsync(ItemInput input, CancellationToken cancellationToken)
    {
        var item = new Item(Guid.NewGuid(), input.Name, input.Kind, input.Note, input.Tags);
        items[item.Id] = item;
        return TypedResults.Created($"/api/items/{item.Id}", item);
    }

    public async Task<Results<NoContent, NotFound<ProblemDetails>>> DeleteItemAsync(Guid id, CancellationToken cancellationToken) =>
        items.TryRemove(id, out _) ? TypedResults.NoContent() : TypedResults.NotFound(Missing(id));

    private static ProblemDetails Missing(Guid id) => new() { Detail = $"No item {id}." };
}

[JsonSerializable(typeof(Item))]
internal partial class Context : JsonSerializerContext;
`);
  dotnetRun(join(directory, "Smoke.csproj"));
}));
