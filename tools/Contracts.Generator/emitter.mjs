import {
  $doc, emitFile, getDoc, getFormat, getLocationContext, getNamespaceFullName, isArrayModelType,
  isNullType, isStdNamespace, isTemplateDeclaration, listServices, navigateProgram,
} from "@typespec/compiler";
import { getAllHttpServices, isBody, isBodyRoot, isMetadata } from "@typespec/http";
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
const httpLibrary = "Manufacturing.Http";
const files = { models: "Models.g.cs", client: "Client.g.cs", server: "Server.g.cs" };
// ASP.NET Core typed results; each status code maps to one result type so handlers cannot drift from the contract.
const typedResults = new Map([
  [200, "Ok"], [201, "Created"], [202, "Accepted"], [204, "NoContent"],
  [400, "BadRequest"], [404, "NotFound"], [409, "Conflict"], [422, "UnprocessableEntity"],
]);
const json = "global::System.Text.Json.Serialization";
const task = "global::System.Threading.Tasks.Task";
const token = "global::System.Threading.CancellationToken";
const mvc = "global::Microsoft.AspNetCore.Mvc";

export async function $onEmit(context) {
  const { program, emitterOutputDir } = context;
  const outputs = context.options.outputs ?? ["models"];
  const declarations = [];
  const supportedRecords = program.stateMap(records);
  const serviceNamespaces = new Set(listServices(program).map((service) => service.type));
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
  const pascal = (name) => name.charAt(0).toUpperCase() + name.slice(1);
  const camel = (name) => name.charAt(0).toLowerCase() + name.slice(1);
  function namespace(type) {
    const name = getNamespaceFullName(type.namespace);
    if (!name) fail(type, "C# declarations must have a namespace.");
    return name.split(".").map((part) => identifier(part, type)).join(".");
  }
  function isProblemDetails(type) {
    return type.kind === "Model" && type.name === "ProblemDetails" &&
      getNamespaceFullName(type.namespace) === httpLibrary;
  }
  function typeName(type, target, server = false) {
    if (type.kind === "TemplateParameter") return identifier(type.node.id.sv, target);
    if (type.kind === "Union" && !type.name) {
      const variants = [...type.variants.values()].map((variant) => variant.type);
      const values = variants.filter((variant) => !isNullType(variant));
      if (variants.length === 2 && values.length === 1 &&
          !["Union", "TemplateParameter"].includes(values[0].kind)) {
        return `${typeName(values[0], target, server)}?`;
      }
    }
    if (type.kind === "Scalar") {
      if (getFormat(program, type) === "uuid" && type.baseScalar?.name === "string" &&
          isStdNamespace(type.baseScalar.namespace)) return "global::System.Guid";
      if (isStdNamespace(type.namespace) && primitives.has(type.name)) return primitives.get(type.name);
    }
    if (type.kind === "Model" && isArrayModelType(type)) return `${typeName(type.indexer.value, target, server)}[]`;
    if (server && isProblemDetails(type)) return `${mvc}.ProblemDetails`;
    if (type.kind === "Enum" || (type.kind === "Model" && recordSealed(type) !== undefined)) {
      const args = type.templateMapper?.args;
      const suffix = args ? `<${args.map((arg) => typeName(arg, target, server)).join(", ")}>` : "";
      return `global::${namespace(type)}.${identifier(type.name, type)}${suffix}`;
    }
    fail(target, `Unsupported C# type: ${type.kind} ${type.name ?? ""}`);
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
  function isHttpEnvelope(model) {
    return [...model.properties.values()].some((property) =>
      isMetadata(program, property) || isBody(program, property) || isBodyRoot(program, property));
  }
  navigateProgram(program, {
    model(model) {
      if (!isProject(model) || model.templateMapper || !model.name) return;
      if (recordSealed(model) === undefined &&
          (getNamespaceFullName(model.namespace) === httpLibrary || isHttpEnvelope(model))) return;
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
      const names = new Set();
      for (const property of model.properties.values()) {
        const name = identifier(pascal(property.name), property);
        if (name === model.name || name === "Clone" || names.has(name) ||
            parameters.some((parameter) => parameter.id.sv === name)) {
          fail(property, `Property ${property.name} conflicts with its record, another property or generic parameters.`);
        }
        names.add(name);
        if (property.optional || property.defaultValue !== undefined) {
          fail(property, "Optional fields and defaults require an explicit C# serialization policy and are not supported.");
        }
        // The TypeSpec name is the wire name; C# gets PascalCase plus an explicit JSON name when they differ.
        const wire = name.replace(/^@/, "") === property.name ? "" : `[property: ${json}.JsonPropertyName(${JSON.stringify(property.name)})] `;
        properties.push(`    ${wire}${typeName(property.type, property)} ${name}`);
      }
      const modifier = recordSealed(model) ? "sealed " : "";
      const suffix = parameters.length ? `<${parameters.map((parameter) => identifier(parameter.id.sv, parameter)).join(", ")}>` : "";
      add(model, `public ${modifier}record ${identifier(model.name, model)}${suffix}(\n${properties.join(",\n")});`);
    },
    enum(type) {
      if (!isProject(type)) return;
      const values = [...type.members.values()];
      // Members without values are string enums (wire value = member name); otherwise explicit int32 values.
      const names = values.every((member) => member.value === undefined);
      const members = [];
      for (const member of values) {
        if (!names && (!Number.isInteger(member.value) || member.value < -2147483648 || member.value > 2147483647)) {
          fail(member, "Enum members must either all omit values (string enum) or all have explicit int32 values.");
        }
        members.push(`    ${identifier(member.name, member)}${names ? "" : ` = ${member.value}`},`);
      }
      const name = identifier(type.name, type);
      const converter = names
        ? `[${json}.JsonConverter(typeof(${json}.JsonStringEnumConverter<global::${namespace(type)}.${name}>))]\n`
        : "";
      add(type, `${converter}public enum ${name}\n{\n${members.join("\n")}\n}`);
    },
    interface(type) {
      if (isProject(type) && isTemplateDeclaration(type)) fail(type, "Generic interfaces are not supported.");
    },
    operation(type) {
      if (!isProject(type)) return;
      if (isTemplateDeclaration(type) || !type.interface || !serviceNamespaces.has(type.interface.namespace)) {
        fail(type, "Operations must be non-generic members of an interface declared directly in an @service namespace.");
      }
    },
    union(type) {
      if (isProject(type) && type.name) fail(type, "Named unions require an explicit C# serialization policy and are not supported.");
    },
  }, { includeTemplateDeclaration: true });

  const contents = {};
  if (outputs.includes("models")) {
    if (declarations.length === 0) fail(program.getGlobalNamespaceType(), "No C# model declarations found.");
    contents.models = render(declarations);
  }
  if (outputs.includes("client") || outputs.includes("server")) {
    const service = httpService();
    if (service) {
      if (outputs.includes("client")) contents.client = render(client(service));
      if (outputs.includes("server")) contents.server = render(server(service));
    }
  }
  if (program.hasError() || program.compilerOptions.noEmit) return;
  for (const [kind, content] of Object.entries(contents)) {
    await emitFile(program, { path: join(emitterOutputDir, files[kind]), content, newLine: "lf" });
  }

  function httpService() {
    const [services, diagnostics] = getAllHttpServices(program);
    program.reportDiagnostics(diagnostics);
    const declared = services.filter((service) => serviceNamespaces.has(service.namespace));
    if (declared.length !== 1 || declared[0].operations.length === 0) {
      fail(program.getGlobalNamespaceType(), "Client and server output need exactly one @service namespace with operations.");
      return undefined;
    }
    const service = declared[0];
    const operations = service.operations.map(operation);
    const names = operations.map((item) => item.name);
    for (const [index, item] of operations.entries()) {
      if (names.indexOf(item.name) !== index) fail(item.source, `Operation names must be unique within the service: ${item.name}`);
    }
    return {
      name: identifier(pascal(getNamespaceFullName(service.namespace).split(".")[0]), service.namespace),
      namespace: getNamespaceFullName(service.namespace).split(".").map((part) => identifier(part, service.namespace)).join("."),
      groups: Map.groupBy(operations, (item) => item.group),
    };
  }
  function sameTypes(actual, expected) {
    return actual.length === expected.length && actual.every((item, index) => item === expected[index]);
  }
  function operation(http) {
    const source = http.operation;
    const parameters = [];
    for (const parameter of http.parameters.parameters) {
      if (parameter.type !== "path" || parameter.param.optional) {
        fail(parameter.param, "Only required path parameters are supported; other parameters need an explicit binding policy.");
      }
      parameters.push({
        wire: parameter.name,
        name: identifier(camel(parameter.param.name), parameter.param),
        type: typeName(parameter.param.type, parameter.param),
      });
    }
    const request = http.parameters.body;
    let body;
    if (request) {
      if (request.bodyKind !== "single" || !request.property || !sameTypes(request.contentTypes, ["application/json"])) {
        fail(source, "Request bodies must be one explicit @body parameter with application/json content.");
      } else {
        body = { name: identifier(camel(request.property.name), request.property), type: typeName(request.type, request.property) };
      }
    }
    // Names used by generated server lambdas and client method bodies.
    const reserved = ["handler", "cancellationToken", "httpClient", "request_", "response_"];
    if ([...parameters, body].some((item) => reserved.includes(item?.name))) {
      fail(source, `Parameters cannot be named ${reserved.join(", ")}.`);
    }
    const responses = [];
    for (const response of http.responses) {
      const status = response.statusCodes;
      if (typeof status !== "number" || response.responses.length > 1 || responses.some((item) => item.status === status)) {
        fail(source, "Each response needs one exact status code with at most one body.");
        continue;
      }
      const content = response.responses[0];
      const headers = Object.keys(content?.headers ?? {}).filter((name) => name.toLowerCase() !== "content-type");
      if (headers.length) fail(source, `Response headers need an explicit C# policy and are not supported: ${headers.join(", ")}`);
      const payload = content?.body;
      const problem = payload !== undefined && isProblemDetails(payload.type);
      if (payload && (payload.bodyKind !== "single" ||
          !sameTypes(payload.contentTypes, [problem ? "application/problem+json" : "application/json"]))) {
        fail(source, `Status ${status} needs a single application/json body or Manufacturing.Http.Problem.`);
      }
      if (problem && status < 400) fail(source, "Problem details are only valid for error status codes.");
      responses.push({
        status,
        client: payload && !problem ? typeName(payload.type, source) : undefined,
        server: payload ? typeName(payload.type, source, true) : undefined,
      });
    }
    const success = responses.filter((item) => item.status < 400);
    if (success.length === 0 || new Set(success.map((item) => item.client)).size !== 1) {
      fail(source, "Successful responses must exist and share one body type.");
    }
    return {
      source,
      group: identifier(http.container.name, http.container),
      name: identifier(pascal(source.name), source),
      verb: http.verb.toUpperCase(),
      path: http.path,
      parameters,
      body,
      responses,
      success,
      doc: doc(source),
    };
  }
  function signature(item, defaultToken) {
    return [
      ...item.parameters.map((parameter) => `${parameter.type} ${parameter.name}`),
      ...(item.body ? [`${item.body.type} ${item.body.name}`] : []),
      `${token} cancellationToken${defaultToken ? " = default" : ""}`,
    ].join(", ");
  }
  function server(service) {
    const items = [];
    const maps = [];
    for (const [group, operations] of service.groups) {
      const handler = `I${group}Handler`;
      const methods = operations.map((item) => {
        const results = item.responses.map((response) => {
          const result = typedResults.get(response.status);
          if (!result || (response.status === 204 && response.server)) {
            fail(item.source, `Status ${response.status} has no supported ASP.NET Core typed result.`);
          }
          return `global::Microsoft.AspNetCore.Http.HttpResults.${result}${response.server ? `<${response.server}>` : ""}`;
        });
        if (results.length > 6) fail(item.source, "ASP.NET Core typed results support at most six responses.");
        const type = results.length === 1 ? results[0] : `global::Microsoft.AspNetCore.Http.HttpResults.Results<${results.join(", ")}>`;
        const lambda = [
          ...item.parameters.map((parameter) => `[${mvc}.FromRoute(Name = ${JSON.stringify(parameter.wire)})] ${parameter.type} ${parameter.name}`),
          ...(item.body ? [`[${mvc}.FromBody] ${item.body.type} ${item.body.name}`] : []),
          `[${mvc}.FromServices] global::${service.namespace}.${handler} handler`,
          `${token} cancellationToken`,
        ].join(", ");
        const args = [...item.parameters.map((parameter) => parameter.name), ...(item.body ? [item.body.name] : []), "cancellationToken"];
        maps.push(`global::Microsoft.AspNetCore.Builder.EndpointRouteBuilderExtensions.MapMethods(group, ${JSON.stringify(item.path)}, [${JSON.stringify(item.verb)}],\n    static (${lambda}) =>\n        handler.${item.name}Async(${args.join(", ")}));`);
        return `${item.doc}${task}<${type}> ${item.name}Async(${signature(item, false)});`;
      });
      items.push({
        namespace: service.namespace,
        text: `/// <summary>\n/// Implements the ${group} operations of the ${service.name} contract.\n/// </summary>\npublic interface ${handler}\n{\n${indent(methods.join("\n\n"))}\n}`,
      });
    }
    items.push({
      namespace: service.namespace,
      text: `public static class ${service.name}ApiEndpoints\n{\n` +
        `    /// <summary>\n    /// Registers RFC 9457 problem details and the JSON rules the ${service.name} contract relies on:\n` +
        `    /// required, non-nullable record properties must be present. Register the handler interfaces separately.\n    /// </summary>\n` +
        `    public static global::Microsoft.Extensions.DependencyInjection.IServiceCollection Add${service.name}Api(this global::Microsoft.Extensions.DependencyInjection.IServiceCollection services)\n    {\n` +
        `        global::Microsoft.Extensions.DependencyInjection.ProblemDetailsServiceCollectionExtensions.AddProblemDetails(services);\n` +
        `        return global::Microsoft.Extensions.DependencyInjection.HttpJsonServiceExtensions.ConfigureHttpJsonOptions(services, options =>\n        {\n` +
        `            options.SerializerOptions.RespectNullableAnnotations = true;\n` +
        `            options.SerializerOptions.RespectRequiredConstructorParameters = true;\n        });\n    }\n\n` +
        `    /// <summary>\n    /// Maps every ${service.name} contract operation to its handler interface.\n    /// </summary>\n` +
        `    public static global::Microsoft.AspNetCore.Routing.RouteGroupBuilder Map${service.name}Api(this global::Microsoft.AspNetCore.Routing.IEndpointRouteBuilder endpoints)\n    {\n` +
        `        var group = global::Microsoft.AspNetCore.Builder.EndpointRouteBuilderExtensions.MapGroup(endpoints, "");\n` +
        `${indent(maps.join("\n"), 2)}\n        return group;\n    }\n}`,
    });
    return items;
  }
  function client(service) {
    const methods = [];
    for (const operations of service.groups.values()) {
      for (const item of operations) {
        const result = item.success[0]?.client;
        const names = new Map(item.parameters.map((parameter) => [parameter.wire, parameter.name]));
        const path = item.path.replace(/^\//, "").replace(/["\\]/g, "\\$&")
          .replace(/\{([^}]+)\}/g, (_, wire) => `{ContractHttp.Path(${names.get(wire)})}`);
        const codes = item.success.map((response) => response.status).join(" or ");
        methods.push(`${item.doc}public async ${task}${result ? `<${result}>` : ""} ${item.name}Async(${signature(item, true)})\n{\n` +
          `    using var request_ = new global::System.Net.Http.HttpRequestMessage(new global::System.Net.Http.HttpMethod(${JSON.stringify(item.verb)}), $"${path}");\n` +
          (item.body ? `    request_.Content = ContractHttp.Json(${item.body.name});\n` : "") +
          `    using var response_ = await httpClient.SendAsync(request_, cancellationToken).ConfigureAwait(false);\n` +
          `    if ((int)response_.StatusCode is ${codes})\n` +
          (result
            ? `        return await ContractHttp.ReadAsync<${result}>(response_, cancellationToken).ConfigureAwait(false);\n`
            : `        return;\n`) +
          `    throw await ContractHttp.ErrorAsync(response_, cancellationToken).ConfigureAwait(false);\n}`);
      }
    }
    return [
      {
        namespace: service.namespace,
        text: `/// <summary>\n/// Typed HTTP client for the ${service.name} contract. Non-success responses throw\n/// <see cref="global::System.Net.Http.HttpRequestException"/> with its StatusCode set.\n/// </summary>\n` +
          `public sealed class ${service.name}Client(global::System.Net.Http.HttpClient httpClient)\n{\n${indent(methods.join("\n\n"))}\n}`,
      },
      {
        namespace: service.namespace,
        text: `file static class ContractHttp\n{\n` +
          `    private static readonly global::System.Text.Json.JsonSerializerOptions Options = new(global::System.Text.Json.JsonSerializerDefaults.Web)\n` +
          `    {\n        RespectNullableAnnotations = true,\n        RespectRequiredConstructorParameters = true,\n    };\n\n` +
          `    internal static string Path<T>(T value) =>\n        global::System.Uri.EscapeDataString(global::System.FormattableString.Invariant($"{value}"));\n\n` +
          `    internal static global::System.Net.Http.HttpContent Json<T>(T value) =>\n        global::System.Net.Http.Json.JsonContent.Create(value, options: Options);\n\n` +
          `    internal static async ${task}<T> ReadAsync<T>(global::System.Net.Http.HttpResponseMessage response, ${token} cancellationToken) =>\n` +
          `        await global::System.Net.Http.Json.HttpContentJsonExtensions.ReadFromJsonAsync<T>(response.Content, Options, cancellationToken).ConfigureAwait(false)\n` +
          `        ?? throw new global::System.Net.Http.HttpRequestException("The response body was empty.", null, response.StatusCode);\n\n` +
          `    internal static async ${task}<global::System.Net.Http.HttpRequestException> ErrorAsync(global::System.Net.Http.HttpResponseMessage response, ${token} cancellationToken)\n    {\n` +
          `        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);\n` +
          `        return new global::System.Net.Http.HttpRequestException(\n` +
          `            $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri} returned {(int)response.StatusCode} {response.ReasonPhrase}: {body}",\n` +
          `            null, response.StatusCode);\n    }\n}`,
      },
    ];
  }
}

function indent(text, depth = 1) {
  const prefix = "    ".repeat(depth);
  return text.split("\n").map((line) => line ? `${prefix}${line}` : "").join("\n");
}

function render(declarations) {
  const namespaces = Map.groupBy(declarations, (item) => item.namespace);
  return "// <auto-generated/>\n#nullable enable\n\n" + [...namespaces].map(([name, items]) =>
    `namespace ${name}\n{\n${items.map((item) => indent(item.text)).join("\n\n")}\n}\n`
  ).join("\n");
}
