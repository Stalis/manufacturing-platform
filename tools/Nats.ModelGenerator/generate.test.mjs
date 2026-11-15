import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import test from "node:test";

const wrapper = fileURLToPath(new URL("./generate.mjs", import.meta.url));
const decorators = fileURLToPath(new URL("./decorators.tsp", import.meta.url)).replaceAll("\\", "/");
const header = `import ${JSON.stringify(decorators)};\nusing Manufacturing.CSharp;\nnamespace Example;\n`;
function run(input, output, ...args) {
  const result = spawnSync(process.execPath, [wrapper, input, output, ...args], { encoding: "utf8" });
  if (result.error) throw result.error;
  return result;
}

for (const declaration of [
  "@csharpRecord(true) model Event { Name?: string; }",
  "@csharpRecord(true) model Event { Name: string = \"default\"; }",
  "@csharpRecord(true) model Event { Name: string | null; }",
  "enum Status { Value: \"text\" }",
  "@csharpRecord(true) model Event { Event: string; }",
  "@csharpRecord(true) model Event { Clone: string; }",
  "@csharpRecord(true) model Event<T> { T: string; }",
  "@csharpRecord(true) model Event<Event> { Value: Event; }",
  "@csharpRecord(true) model Event<T extends string> { Value: T; }",
  "@csharpRecord(true) model Event<T extends valueof string> {}",
  "@csharpRecord(true) model Event { Name: string; } op send<T>(payload: T): void;",
  "@csharpRecord(true) model Event { Name: string; } model Missing<T> { Value: T; }",
  "@csharpRecord(true) model Event {",
]) {
  test(`Failed compilation preserves existing output: ${declaration}`, async () => {
    const directory = await mkdtemp(join(tmpdir(), "nats-model-test-"));
    try {
      const input = join(directory, "main.tsp");
      const output = join(directory, "Models.cs");
      await writeFile(input, header + declaration);
      await writeFile(output, "existing output");
      const result = run(input, output);
      assert.equal(result.status, 1, result.stdout + result.stderr);
      assert.equal(await readFile(output, "utf8"), "existing output");
    } finally {
      await rm(directory, { recursive: true, force: true });
    }
  });
}

test("Check accepts regenerated output and rejects drift without overwriting it", async () => {
  const directory = await mkdtemp(join(tmpdir(), "nats-model-test-"));
  try {
    const input = join(directory, "main.tsp");
    const output = join(directory, "Models.cs");
    await writeFile(input, header + "@csharpRecord(true) model Event { Name: string; }");
    const generated = run(input, output);
    assert.equal(generated.status, 0, generated.stdout + generated.stderr);
    const checked = run(input, output, "--check");
    assert.equal(checked.status, 0, checked.stdout + checked.stderr);
    await writeFile(output, "stale output");
    assert.equal(run(input, output, "--check").status, 1);
    assert.equal(await readFile(output, "utf8"), "stale output");
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("Generic records compile and closed nested types preserve JSON data", async () => {
  const directory = await mkdtemp(join(tmpdir(), "nats-model-test-"));
  try {
    const input = join(directory, "main.tsp");
    const output = join(directory, "Models.cs");
    await writeFile(input, header + `
@csharpRecord(false) model Cell<T> { Value: T; }
@csharpRecord(true) model Pair<A, B = int32> { First: Cell<A>; Second: B; }
@csharpRecord(true) model Unused<T> { Value: T; }
enum Status { Ready: 7 }
@csharpRecord(true) model Event { Data: Pair<string>; State: Cell<Status>; }
`);
    const generated = run(input, output);
    assert.equal(generated.status, 0, generated.stdout + generated.stderr);
    await writeFile(join(directory, "Smoke.csproj"), `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup>
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
    const smoke = spawnSync("dotnet", ["run", "--project", join(directory, "Smoke.csproj"), "--nologo"],
      { encoding: "utf8", timeout: 60000 });
    if (smoke.error) throw smoke.error;
    assert.equal(smoke.status, 0, smoke.stdout + smoke.stderr);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});
