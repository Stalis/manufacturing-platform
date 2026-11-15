import { compile, logDiagnostics, NodeHost } from "@typespec/compiler";
import { mkdir, mkdtemp, readFile, rename, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

// openapi output is a development artifact and is never compared by --check.
const files = { models: "Models.g.cs", client: "Client.g.cs", server: "Server.g.cs", openapi: "openapi.json" };
const usage = "Usage: node generate.mjs <input.tsp> <kind>=<output> [<kind>=<output>...] [<input.tsp> ...] [--check]\n" +
  `Kinds: ${Object.keys(files).join(", ")}`;

const args = process.argv.slice(2);
const check = args.includes("--check");
const targets = [];
let valid = true;
for (const arg of args.filter((item) => item !== "--check")) {
  const output = /^([a-z]+)=(.+)$/.exec(arg);
  if (output) {
    const target = targets.at(-1);
    if (!target || !(output[1] in files) || output[1] in target.outputs) valid = false;
    else target.outputs[output[1]] = resolve(output[2]);
  } else if (arg.startsWith("--")) {
    valid = false;
  } else {
    targets.push({ input: resolve(arg), outputs: {} });
  }
}

if (!valid || targets.length === 0 || targets.some((target) => Object.keys(target.outputs).length === 0)) {
  console.error(usage);
  process.exitCode = 1;
} else {
  const here = dirname(fileURLToPath(import.meta.url));
  const emitter = join(here, "emitter.mjs");
  const openapi = join(here, "node_modules", "@typespec", "openapi3");
  const staging = await mkdtemp(join(tmpdir(), "contracts-"));
  const replacements = [];
  try {
    const generated = [];
    let failed = false;
    for (const [index, target] of targets.entries()) {
      const directory = join(staging, String(index));
      const csharp = Object.keys(target.outputs).filter((kind) => kind !== "openapi");
      const emit = [];
      const options = {};
      if (csharp.length) {
        emit.push(emitter);
        options[emitter] = { "emitter-output-dir": directory, outputs: csharp };
      }
      if (target.outputs.openapi) {
        emit.push(openapi);
        // Library emitters read options by package name, even when loaded from a path.
        options["@typespec/openapi3"] = { "emitter-output-dir": directory, "output-file": files.openapi, "file-type": "json" };
      }
      const program = await compile(NodeHost, target.input, { emit, outputDir: directory, options, warningAsError: true });
      logDiagnostics(program.diagnostics, NodeHost.logSink);
      if (program.hasError()) {
        failed = true;
        continue;
      }
      for (const [kind, output] of Object.entries(target.outputs)) {
        generated.push({ kind, output, content: await readFile(join(directory, files[kind]), "utf8") });
      }
    }
    if (failed) {
      process.exitCode = 1;
    } else if (check) {
      const stale = [];
      for (const { kind, output, content } of generated) {
        if (kind !== "openapi" && content !== await readFile(output, "utf8").catch(() => undefined)) stale.push(output);
      }
      if (stale.length) throw new Error(`Generated output is out of date:\n${stale.join("\n")}`);
      console.log(`Up to date: ${generated.filter((item) => item.kind !== "openapi").length} file(s)`);
    } else {
      // Replace only after every input compiles; stage beside each destination for an atomic rename.
      for (const { output, content } of generated) {
        await mkdir(dirname(output), { recursive: true });
        const replacement = await mkdtemp(join(dirname(output), ".contracts-"));
        replacements.push(replacement);
        await writeFile(join(replacement, "file"), content);
        await rename(join(replacement, "file"), output);
        console.log(`Generated: ${output}`);
      }
    }
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  } finally {
    await rm(staging, { recursive: true, force: true });
    for (const replacement of replacements) await rm(replacement, { recursive: true, force: true });
  }
}
