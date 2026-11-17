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

/** File name each emitter output kind is written to inside the emitter output directory. */
export const files = { models: "Models.g.cs", client: "Client.g.cs", server: "Server.g.cs" };

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
// ASP.NET Core typed results; each status code maps to one result type so handlers cannot drift from the contract.
const typedResults = new Map([
  [200, "Ok"], [201, "Created"], [202, "Accepted"], [204, "NoContent"],
  [400, "BadRequest"], [404, "NotFound"], [409, "Conflict"], [422, "UnprocessableEntity"],
]);
const json = "global::System.Text.Json.Serialization";
const task = "global::System.Threading.Tasks.Task";
const token = "global::System.Threading.CancellationToken";
const mvc = "global::Microsoft.AspNetCore.Mvc";
const http = "global::Microsoft.AspNetCore.Http";
const results = `${http}.HttpResults`;
const enumConverter = "StrictStringEnumConverter";

export async function $onEmit(context) {
  const { program, emitterOutputDir } = context;
  const outputs = context.options.outputs ?? ["models"];
  const ctx = {
    program,
    records: program.stateMap(records),
    serviceNamespaces: new Set(listServices(program).map((service) => service.type)),
  };
  const declarations = collectModels(ctx);
  const contents = {};
  if (outputs.includes("models")) {
    if (declarations.length === 0) fail(ctx, program.getGlobalNamespaceType(), "No C# model declarations found.");
    contents.models = render(declarations);
  }
  if (outputs.includes("client") || outputs.includes("server")) {
    const service = httpService(ctx);
    if (service) {
      if (outputs.includes("client")) contents.client = render(renderClient(service));
      if (outputs.includes("server")) contents.server = render(renderServer(ctx, service));
    }
  }
  if (program.hasError() || program.compilerOptions.noEmit) return;
  for (const [kind, content] of Object.entries(contents)) {
    await emitFile(program, { path: join(emitterOutputDir, files[kind]), content, newLine: "lf" });
  }
}

function recordSealed(ctx, type) {
  // Decorators are recorded but not executed on unfinished template declarations.
  return ctx.records.get(type) ?? type.decorators.find(
    (item) => item.decorator === $decorators["Manufacturing.CSharp"].csharpRecord
  )?.args[0].jsValue;
}

function fail(ctx, target, message) {
  ctx.program.reportDiagnostic({ code: "manufacturing-csharp/unsupported", severity: "error", target, message });
}

function identifier(ctx, name, target) {
  if (!/^[A-Za-z_][A-Za-z0-9_]*$/.test(name)) {
    fail(ctx, target, `Not a supported C# identifier: ${name}`);
  }
  return keywords.has(name) ? `@${name}` : name;
}

const pascal = (name) => name.charAt(0).toUpperCase() + name.slice(1);
const camel = (name) => name.charAt(0).toLowerCase() + name.slice(1);

function namespace(ctx, type) {
  const name = getNamespaceFullName(type.namespace);
  if (!name) fail(ctx, type, "C# declarations must have a namespace.");
  return name.split(".").map((part) => identifier(ctx, part, type)).join(".");
}

function isProblemDetails(type) {
  return type.kind === "Model" && type.name === "ProblemDetails" &&
    getNamespaceFullName(type.namespace) === httpLibrary;
}

function typeName(ctx, type, target, server = false) {
  if (type.kind === "TemplateParameter") return identifier(ctx, type.node.id.sv, target);
  if (type.kind === "Union" && !type.name) {
    const variants = [...type.variants.values()].map((variant) => variant.type);
    const values = variants.filter((variant) => !isNullType(variant));
    if (variants.length === 2 && values.length === 1 &&
        !["Union", "TemplateParameter"].includes(values[0].kind)) {
      return `${typeName(ctx, values[0], target, server)}?`;
    }
  }
  if (type.kind === "Scalar") {
    if (getFormat(ctx.program, type) === "uuid" && type.baseScalar?.name === "string" &&
        isStdNamespace(type.baseScalar.namespace)) return "global::System.Guid";
    if (isStdNamespace(type.namespace) && primitives.has(type.name)) return primitives.get(type.name);
  }
  if (type.kind === "Model" && isArrayModelType(type)) return `${typeName(ctx, type.indexer.value, target, server)}[]`;
  if (server && isProblemDetails(type)) return `${mvc}.ProblemDetails`;
  if (type.kind === "Enum" || (type.kind === "Model" && recordSealed(ctx, type) !== undefined)) {
    const args = type.templateMapper?.args;
    const suffix = args ? `<${args.map((arg) => typeName(ctx, arg, target, server)).join(", ")}>` : "";
    return `global::${namespace(ctx, type)}.${identifier(ctx, type.name, type)}${suffix}`;
  }
  fail(ctx, target, `Unsupported C# type: ${type.kind} ${type.name ?? ""}`);
  return "";
}

function doc(ctx, type) {
  const text = getDoc(ctx.program, type) ?? type.decorators.find((item) => item.decorator === $doc)?.args[0].jsValue;
  if (!text) return "";
  const escaped = text.replaceAll("&", "&amp;").replaceAll("<", "&lt;").replaceAll(">", "&gt;");
  return `/// <summary>\n${escaped.split(/\r?\n/).map((line) => `/// ${line}`).join("\n")}\n/// </summary>\n`;
}

function isProject(ctx, type) {
  return getLocationContext(ctx.program, type).type === "project" && !isStdNamespace(type.namespace);
}

function isHttpEnvelope(ctx, model) {
  return [...model.properties.values()].some((property) =>
    isMetadata(ctx.program, property) || isBody(ctx.program, property) || isBodyRoot(ctx.program, property));
}

/** Validates project declarations and returns the C# records and enums of the models output. */
function collectModels(ctx) {
  const declarations = [];
  const converters = new Set();
  const add = (type, text) => declarations.push({ namespace: namespace(ctx, type), text: `${doc(ctx, type)}${text}` });
  navigateProgram(ctx.program, {
    model(model) {
      if (!isProject(ctx, model) || model.templateMapper || !model.name) return;
      const sealed = recordSealed(ctx, model);
      if (sealed === undefined &&
          (getNamespaceFullName(model.namespace) === httpLibrary || isHttpEnvelope(ctx, model))) return;
      if (sealed === undefined || model.baseModel || model.indexer) {
        fail(ctx, model, "Only models annotated with @csharpRecord, without inheritance or indexers, are supported.");
        return;
      }
      const parameters = isTemplateDeclaration(model) ? model.node.templateParameters : [];
      for (const parameter of parameters) {
        if (parameter.constraint) {
          fail(ctx, parameter, "Generic parameter constraints require an explicit C# policy and are not supported.");
        }
        if (parameter.id.sv === model.name) fail(ctx, parameter, "A generic parameter cannot have the record's name.");
      }
      const properties = [];
      const names = new Set();
      for (const property of model.properties.values()) {
        const name = identifier(ctx, pascal(property.name), property);
        if (name === model.name || name === "Clone" || names.has(name) ||
            parameters.some((parameter) => parameter.id.sv === name)) {
          fail(ctx, property, `Property ${property.name} conflicts with its record, another property or generic parameters.`);
        }
        names.add(name);
        if (property.optional || property.defaultValue !== undefined) {
          fail(ctx, property, "Optional fields and defaults require an explicit C# serialization policy and are not supported.");
        }
        // The TypeSpec name is the wire name; C# gets PascalCase plus an explicit JSON name when they differ.
        const wire = name.replace(/^@/, "") === property.name ? "" : `[property: ${json}.JsonPropertyName(${JSON.stringify(property.name)})] `;
        properties.push(`    ${wire}${typeName(ctx, property.type, property)} ${name}`);
      }
      const modifier = sealed ? "sealed " : "";
      const suffix = parameters.length ? `<${parameters.map((parameter) => identifier(ctx, parameter.id.sv, parameter)).join(", ")}>` : "";
      add(model, `public ${modifier}record ${identifier(ctx, model.name, model)}${suffix}(\n${properties.join(",\n")});`);
    },
    enum(type) {
      if (!isProject(ctx, type)) return;
      const values = [...type.members.values()];
      // Members without values are string enums (wire value = member name); otherwise explicit int32 values.
      const names = values.every((member) => member.value === undefined);
      const members = [];
      for (const member of values) {
        if (!names && (!Number.isInteger(member.value) || member.value < -2147483648 || member.value > 2147483647)) {
          fail(ctx, member, "Enum members must either all omit values (string enum) or all have explicit int32 values.");
        }
        members.push(`    ${identifier(ctx, member.name, member)}${names ? "" : ` = ${member.value}`},`);
      }
      const name = identifier(ctx, type.name, type);
      const scope = namespace(ctx, type);
      if (names && !converters.has(scope)) {
        converters.add(scope);
        // Public so System.Text.Json source generators in consuming assemblies can instantiate it.
        declarations.push({
          namespace: scope,
          text: `/// <summary>\n/// Serializes contract enums by member name and rejects integer JSON values.\n/// </summary>\n` +
            `public sealed class ${enumConverter}<TEnum>() : ${json}.JsonStringEnumConverter<TEnum>(namingPolicy: null, allowIntegerValues: false)\n` +
            `    where TEnum : struct, global::System.Enum;`,
        });
      }
      const converter = names ? `[${json}.JsonConverter(typeof(global::${scope}.${enumConverter}<global::${scope}.${name}>))]\n` : "";
      add(type, `${converter}public enum ${name}\n{\n${members.join("\n")}\n}`);
    },
    interface(type) {
      if (isProject(ctx, type) && isTemplateDeclaration(type)) fail(ctx, type, "Generic interfaces are not supported.");
    },
    operation(type) {
      if (!isProject(ctx, type)) return;
      if (isTemplateDeclaration(type) || !type.interface || !ctx.serviceNamespaces.has(type.interface.namespace)) {
        fail(ctx, type, "Operations must be non-generic members of an interface declared directly in an @service namespace.");
      }
    },
    union(type) {
      if (isProject(ctx, type) && type.name) fail(ctx, type, "Named unions require an explicit C# serialization policy and are not supported.");
    },
  }, { includeTemplateDeclaration: true });
  return declarations;
}

/** Resolves the single @service namespace and analyzes its operations, grouped by interface. */
function httpService(ctx) {
  const { program } = ctx;
  const [services, diagnostics] = getAllHttpServices(program);
  program.reportDiagnostics(diagnostics);
  const declared = services.filter((service) => ctx.serviceNamespaces.has(service.namespace));
  if (declared.length !== 1 || declared[0].operations.length === 0) {
    fail(ctx, program.getGlobalNamespaceType(), "Client and server output need exactly one @service namespace with operations.");
    return undefined;
  }
  const service = declared[0];
  const operations = service.operations.map((item) => analyzeOperation(ctx, item));
  const names = operations.map((item) => item.name);
  for (const [index, item] of operations.entries()) {
    if (names.indexOf(item.name) !== index) fail(ctx, item.source, `Operation names must be unique within the service: ${item.name}`);
  }
  const name = identifier(ctx, pascal(getNamespaceFullName(service.namespace).split(".")[0]), service.namespace);
  return {
    name,
    namespace: getNamespaceFullName(service.namespace).split(".").map((part) => identifier(ctx, part, service.namespace)).join("."),
    // Server code belongs to the API project; DTOs and the client stay in the contract namespace.
    serverNamespace: `${name}.Api`,
    groups: Map.groupBy(operations, (item) => item.group),
  };
}

function sameTypes(actual, expected) {
  return actual.length === expected.length && actual.every((item, index) => item === expected[index]);
}

function analyzeOperation(ctx, operation) {
  const source = operation.operation;
  const parameters = [];
  for (const parameter of operation.parameters.parameters) {
    if (parameter.type !== "path" || parameter.param.optional) {
      fail(ctx, parameter.param, "Only required path parameters are supported; other parameters need an explicit binding policy.");
    }
    parameters.push({
      wire: parameter.name,
      name: identifier(ctx, camel(parameter.param.name), parameter.param),
      type: typeName(ctx, parameter.param.type, parameter.param),
    });
  }
  const request = operation.parameters.body;
  let body;
  if (request) {
    if (request.bodyKind !== "single" || !request.property || !sameTypes(request.contentTypes, ["application/json"])) {
      fail(ctx, source, "Request bodies must be one explicit @body parameter with application/json content.");
    } else {
      body = { name: identifier(ctx, camel(request.property.name), request.property), type: typeName(ctx, request.type, request.property) };
    }
  }
  // Names used by generated server lambdas and client method bodies.
  const reserved = ["handler", "cancellationToken", "httpClient", "request_", "response_"];
  if ([...parameters, body].some((item) => reserved.includes(item?.name))) {
    fail(ctx, source, `Parameters cannot be named ${reserved.join(", ")}.`);
  }
  const responses = [];
  for (const response of operation.responses) {
    const status = response.statusCodes;
    if (typeof status !== "number" || response.responses.length > 1 || responses.some((item) => item.status === status)) {
      fail(ctx, source, "Each response needs one exact status code with at most one body.");
      continue;
    }
    const content = response.responses[0];
    const headers = Object.keys(content?.headers ?? {}).filter((name) => name.toLowerCase() !== "content-type");
    if (headers.length) fail(ctx, source, `Response headers need an explicit C# policy and are not supported: ${headers.join(", ")}`);
    const payload = content?.body;
    const problem = payload !== undefined && isProblemDetails(payload.type);
    if (payload && (payload.bodyKind !== "single" ||
        !sameTypes(payload.contentTypes, [problem ? "application/problem+json" : "application/json"]))) {
      fail(ctx, source, `Status ${status} needs a single application/json body or Manufacturing.Http.Problem.`);
    }
    if (problem && status < 400) fail(ctx, source, "Problem details are only valid for error status codes.");
    responses.push({
      status,
      client: payload && !problem ? typeName(ctx, payload.type, source) : undefined,
      server: payload ? typeName(ctx, payload.type, source, true) : undefined,
    });
  }
  const success = responses.filter((item) => item.status < 400);
  if (success.length === 0 || new Set(success.map((item) => item.client)).size !== 1) {
    fail(ctx, source, "Successful responses must exist and share one body type.");
  }
  return {
    source,
    group: identifier(ctx, operation.container.name, operation.container),
    name: identifier(ctx, pascal(source.name), source),
    verb: operation.verb.toUpperCase(),
    path: operation.path,
    parameters,
    body,
    responses,
    success,
    doc: doc(ctx, source),
  };
}

function signature(item, defaultToken) {
  return [
    ...item.parameters.map((parameter) => `${parameter.type} ${parameter.name}`),
    ...(item.body ? [`${item.body.type} ${item.body.name}`] : []),
    `${token} cancellationToken${defaultToken ? " = default" : ""}`,
  ].join(", ");
}

function renderServer(ctx, service) {
  const items = [];
  const maps = [];
  for (const [group, operations] of service.groups) {
    const handler = `I${group}Handler`;
    const methods = operations.map((item) => {
      const types = item.responses.map((response) => {
        const result = typedResults.get(response.status);
        if (!result || (response.status === 204 && response.server)) {
          fail(ctx, item.source, `Status ${response.status} has no supported ASP.NET Core typed result.`);
        }
        return `${results}.${result}${response.server ? `<${response.server}>` : ""}`;
      });
      if (types.length > 6) fail(ctx, item.source, "ASP.NET Core typed results support at most six responses.");
      const type = types.length === 1 ? types[0] : `${results}.Results<${types.join(", ")}>`;
      const lambda = [
        ...item.parameters.map((parameter) => `[${mvc}.FromRoute(Name = ${JSON.stringify(parameter.wire)})] ${parameter.type} ${parameter.name}`),
        ...(item.body ? [`[${mvc}.FromBody] ${item.body.type} ${item.body.name}`] : []),
        `[${mvc}.FromServices] global::${service.serverNamespace}.${handler} handler`,
        `${token} cancellationToken`,
      ].join(", ");
      const args = [...item.parameters.map((parameter) => parameter.name), ...(item.body ? [item.body.name] : []), "cancellationToken"];
      maps.push(`global::Microsoft.AspNetCore.Builder.EndpointRouteBuilderExtensions.MapMethods(group, ${JSON.stringify(item.path)}, [${JSON.stringify(item.verb)}],\n    static (${lambda}) =>\n        handler.${item.name}Async(${args.join(", ")}));`);
      return `${item.doc}${task}<${type}> ${item.name}Async(${signature(item, false)});`;
    });
    items.push({
      namespace: service.serverNamespace,
      text: `/// <summary>\n/// Implements the ${group} operations of the ${service.name} contract.\n/// </summary>\npublic interface ${handler}\n{\n${indent(methods.join("\n\n"))}\n}`,
    });
  }
  const services = "global::Microsoft.Extensions.DependencyInjection.IServiceCollection";
  items.push({
    namespace: service.serverNamespace,
    text: `public static class ${service.name}ApiEndpoints\n{\n` +
      `    /// <summary>\n    /// Registers RFC 9457 problem details and the JSON rules the ${service.name} contract relies on:\n` +
      `    /// required, non-nullable record properties must be present. Register the handler interfaces separately.\n    /// </summary>\n` +
      `    public static ${services} Add${service.name}Api(this ${services} services)\n    {\n` +
      `        global::Microsoft.Extensions.DependencyInjection.ProblemDetailsServiceCollectionExtensions.AddProblemDetails(services);\n` +
      `        return global::Microsoft.Extensions.DependencyInjection.HttpJsonServiceExtensions.ConfigureHttpJsonOptions(services, options =>\n        {\n` +
      `            options.SerializerOptions.RespectNullableAnnotations = true;\n` +
      `            options.SerializerOptions.RespectRequiredConstructorParameters = true;\n        });\n    }\n\n` +
      `    /// <summary>\n    /// Maps every ${service.name} contract operation to its handler interface. In Development it also serves\n` +
      `    /// the generated OpenAPI document from the content root at /openapi/v1.json.\n    /// </summary>\n` +
      `    public static global::Microsoft.AspNetCore.Routing.RouteGroupBuilder Map${service.name}Api(this global::Microsoft.AspNetCore.Routing.IEndpointRouteBuilder endpoints)\n    {\n` +
      `        var environment = global::Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<global::Microsoft.Extensions.Hosting.IHostEnvironment>(endpoints.ServiceProvider);\n` +
      `        if (global::Microsoft.Extensions.Hosting.HostEnvironmentEnvExtensions.IsDevelopment(environment))\n        {\n` +
      `            // Written by tools/Contracts.Generator and git-ignored.\n` +
      `            var openApi = global::System.IO.Path.Combine(environment.ContentRootPath, "openapi.json");\n` +
      `            global::Microsoft.AspNetCore.Builder.EndpointRouteBuilderExtensions.MapGet(endpoints, "/openapi/v1.json", () => global::System.IO.File.Exists(openApi)\n` +
      `                ? ${http}.Results.File(openApi, "application/json")\n` +
      `                : ${http}.Results.Problem("Run \`npm --prefix tools/Contracts.Generator run generate\`.", statusCode: 404));\n        }\n\n` +
      `        var group = global::Microsoft.AspNetCore.Builder.EndpointRouteBuilderExtensions.MapGroup(endpoints, "");\n` +
      `${indent(maps.join("\n"), 2)}\n        return group;\n    }\n\n` +
      `    /// <summary>\n    /// Problem details carrying only <paramref name="detail"/>; the problem details service fills in status and title.\n    /// </summary>\n` +
      `    public static ${mvc}.ProblemDetails Problem(string detail) => new() { Detail = detail };\n\n` +
      `    /// <summary>\n    /// Maps an enum value to the member of <typeparamref name="TEnum"/> with the same name.\n` +
      `    /// Throws <see cref="global::System.InvalidOperationException"/> for undefined values and names without a counterpart.\n    /// </summary>\n` +
      `    public static TEnum MapEnum<TEnum>(global::System.Enum value) where TEnum : struct, global::System.Enum =>\n` +
      `        global::System.Enum.GetName(value.GetType(), value) is { } name && global::System.Enum.TryParse<TEnum>(name, out var mapped)\n` +
      `            ? mapped\n` +
      `            : throw new global::System.InvalidOperationException($"{value.GetType().Name} value {value} has no {typeof(TEnum).Name} counterpart.");\n}`,
  });
  return items;
}

function renderClient(service) {
  const methods = [];
  for (const operations of service.groups.values()) {
    for (const item of operations) {
      const result = item.success[0]?.client;
      const names = new Map(item.parameters.map((parameter) => [parameter.wire, parameter.name]));
      const path = item.path.replace(/^\//, "").replace(/["\\]/g, "\\$&")
        .replace(/\{([^{}]+)\}/g, (_, wire) => `{ContractHttp.Path(${names.get(wire)})}`);
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
