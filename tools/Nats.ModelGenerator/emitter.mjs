import {
  $doc, emitFile, getDoc, getFormat, getLocationContext, getNamespaceFullName,
  isStdNamespace, isTemplateDeclaration, navigateProgram,
} from "@typespec/compiler";
import { join } from "node:path";

const records = Symbol("csharp-records");
export const $decorators = {
  "Manufacturing.CSharp": {
    csharpRecord(context, target, sealed) {
      context.program.stateMap(records).set(target, sealed);
    },
  },
};

const keywords = new Set((
  "abstract as base bool break byte case catch char checked class const continue decimal " +
  "default delegate do double else enum event explicit extern false finally fixed float for " +
  "foreach goto if implicit in int interface internal is lock long namespace new null object " +
  "operator out override params private protected public readonly ref return sbyte sealed short " +
  "sizeof stackalloc static string struct switch this throw true try typeof uint ulong unchecked " +
  "unsafe ushort using virtual void volatile while"
).split(" "));
const primitives = new Map([
  ["string", "string"], ["boolean", "bool"], ["int32", "int"], ["uint32", "uint"],
  ["int64", "long"], ["uint64", "ulong"], ["float64", "double"], ["decimal", "decimal"],
]);

export async function $onEmit({ program, emitterOutputDir }) {
  const declarations = [];
  const supportedRecords = program.stateMap(records);
  function recordSealed(type) {
    // Decorators are recorded but not executed on unfinished template declarations.
    return supportedRecords.get(type) ?? type.decorators.find(
      (item) => item.decorator === $decorators["Manufacturing.CSharp"].csharpRecord
    )?.args[0].jsValue;
  }
  function fail(target, message) {
    program.reportDiagnostic({ code: "manufacturing-csharp/unsupported", severity: "error", target, message });
  }
  function identifier(name, target) {
    if (!/^[A-Za-z_][A-Za-z0-9_]*$/.test(name)) {
      fail(target, `Not a supported C# identifier: ${name}`);
    }
    return keywords.has(name) ? `@${name}` : name;
  }
  function namespace(type) {
    const name = getNamespaceFullName(type.namespace);
    if (!name) fail(type, "C# declarations must have a namespace.");
    return name.split(".").map((part) => identifier(part, type)).join(".");
  }
  function typeName(type, target) {
    if (type.kind === "TemplateParameter") return identifier(type.node.id.sv, target);
    if (type.kind === "Scalar") {
      if (getFormat(program, type) === "uuid" && type.baseScalar?.name === "string" &&
          isStdNamespace(type.baseScalar.namespace)) return "global::System.Guid";
      if (isStdNamespace(type.namespace) && primitives.has(type.name)) return primitives.get(type.name);
    }
    if (type.kind === "Enum" || (type.kind === "Model" && recordSealed(type) !== undefined)) {
      const args = type.templateMapper?.args;
      const suffix = args ? `<${args.map((arg) => typeName(arg, target)).join(", ")}>` : "";
      return `global::${namespace(type)}.${identifier(type.name, type)}${suffix}`;
    }
    fail(target, `Unsupported C# field type: ${type.kind} ${type.name ?? ""}`);
    return "";
  }
  function doc(type) {
    const text = getDoc(program, type) ?? type.decorators.find((item) => item.decorator === $doc)?.args[0].jsValue;
    if (!text) return "";
    const escaped = text.replaceAll("&", "&amp;").replaceAll("<", "&lt;").replaceAll(">", "&gt;");
    return `/// <summary>\n${escaped.split(/\r?\n/).map((line) => `/// ${line}`).join("\n")}\n/// </summary>\n`;
  }
  function add(type, text) {
    declarations.push({ namespace: namespace(type), text: `${doc(type)}${text}` });
  }
  function isProject(type) {
    return getLocationContext(program, type).type === "project" && !isStdNamespace(type.namespace);
  }
  navigateProgram(program, {
    model(model) {
      if (!isProject(model)) return;
      if (model.templateMapper) return;
      if (recordSealed(model) === undefined || model.baseModel || model.indexer) {
        fail(model, "Only models annotated with @csharpRecord, without inheritance or indexers, are supported.");
        return;
      }
      const parameters = isTemplateDeclaration(model) ? model.node.templateParameters : [];
      for (const parameter of parameters) {
        if (parameter.constraint) {
          fail(parameter, "Generic parameter constraints require an explicit C# policy and are not supported.");
        }
        if (parameter.id.sv === model.name) fail(parameter, "A generic parameter cannot have the record's name.");
      }
      const properties = [];
      for (const property of model.properties.values()) {
        if (property.name === model.name || property.name === "Clone" ||
            parameters.some((parameter) => parameter.id.sv === property.name)) {
          fail(property, `Property ${property.name} conflicts with its record or generic parameters.`);
        }
        if (property.optional || property.defaultValue !== undefined) {
          fail(property, "Optional fields and defaults require an explicit C# serialization policy and are not supported.");
        }
        properties.push(`    ${typeName(property.type, property)} ${identifier(property.name, property)}`);
      }
      const modifier = recordSealed(model) ? "sealed " : "";
      const suffix = parameters.length ? `<${parameters.map((parameter) => identifier(parameter.id.sv, parameter)).join(", ")}>` : "";
      add(model, `public ${modifier}record ${identifier(model.name, model)}${suffix}(\n${properties.join(",\n")});`);
    },
    enum(type) {
      if (!isProject(type)) return;
      const members = [];
      for (const member of type.members.values()) {
        if (!Number.isInteger(member.value) || member.value < -2147483648 || member.value > 2147483647) {
          fail(member, "Enum members must have explicit int32 values.");
        }
        members.push(`    ${identifier(member.name, member)} = ${member.value},`);
      }
      add(type, `public enum ${identifier(type.name, type)}\n{\n${members.join("\n")}\n}`);
    },
    operation(type) {
      if (isProject(type)) fail(type, "Operations are not supported by the DTO emitter.");
    },
    union(type) {
      if (isProject(type)) fail(type, "Unions require an explicit C# serialization policy and are not supported.");
    },
  }, { includeTemplateDeclaration: true });
  if (declarations.length === 0) fail(program.getGlobalNamespaceType(), "No C# model declarations found.");
  if (program.hasError() || program.compilerOptions.noEmit) return;
  const namespaces = Map.groupBy(declarations, (item) => item.namespace);
  const content = "// <auto-generated/>\n#nullable enable\n\n" + [...namespaces].map(([name, items]) =>
    `namespace ${name}\n{\n${items.map((item) => item.text.split("\n").map((line) => line ? `    ${line}` : "").join("\n")).join("\n\n")}\n}\n`
  ).join("\n");
  await emitFile(program, { path: join(emitterOutputDir, "Models.g.cs"), content, newLine: "lf" });
}
