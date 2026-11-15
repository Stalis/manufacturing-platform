import { compile, logDiagnostics, NodeHost } from "@typespec/compiler";
import { mkdtemp, readFile, rename, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const args = process.argv.slice(2);
const check = args.includes("--check");
const paths = args.filter((arg) => arg !== "--check");
if (paths.length !== 2 || paths.some((arg) => arg.startsWith("--"))) {
  console.error("Usage: node generate.mjs <input.tsp> <output.cs> [--check]");
  process.exitCode = 1;
} else {
  const staging = await mkdtemp(join(tmpdir(), "nats-models-"));
  let replacement;
  try {
    const emitter = fileURLToPath(new URL("./emitter.mjs", import.meta.url));
    const program = await compile(NodeHost, resolve(paths[0]), {
      emit: [emitter],
      outputDir: staging,
      options: { [emitter]: { "emitter-output-dir": staging } },
      warningAsError: true,
    });
    logDiagnostics(program.diagnostics, NodeHost.logSink);
    if (program.hasError()) {
      process.exitCode = 1;
    } else {
      const content = await readFile(join(staging, "Models.g.cs"), "utf8");
      const output = resolve(paths[1]);
      if (check) {
        if (content !== await readFile(output, "utf8")) {
          throw new Error(`Generated models are out of date: ${output}`);
        }
        console.log(`Up to date: ${output}`);
      } else {
        // Replace only after compilation succeeds; stage beside the destination for an atomic rename.
        replacement = await mkdtemp(join(dirname(output), ".nats-models-"));
        const file = join(replacement, "Models.g.cs");
        await writeFile(file, content);
        await rename(file, output);
        console.log(`Generated: ${output}`);
      }
    }
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  } finally {
    await rm(staging, { recursive: true, force: true });
    if (replacement) await rm(replacement, { recursive: true, force: true });
  }
}
