using System.Text;
using Microsoft.CodeAnalysis.CSharp;

namespace Manufacturing.Nats.Contracts.Generator;

internal static class TypedClients
{
    private static IEnumerable<NatsContractConfig.StreamConfig> Streams(NatsContractConfig.Service service) =>
        new[] { service.Streams.Commands, service.Streams.Events };

    private static IEnumerable<(string Key, NatsContractConfig.SubjectConfig Binding, bool Core)> Subjects(
        NatsContractConfig.Service service) =>
        Streams(service).SelectMany(s => s.Subjects).Select(s => (s.Key, s.Value, false))
            .Concat(service.CoreSubjects.Select(s => (s.Key, s.Value, true)));

    internal static void Validate(NatsContractConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.BaseSubject) || config.BaseSubject.Split('.').Any(t => !SubjectToken(t)))
            throw new InvalidOperationException("base_subject must be a literal NATS subject.");
        if (config.Services.Count == 0)
            throw new InvalidOperationException("At least one service is required.");
        var subjects = new HashSet<string>(StringComparer.Ordinal);
        var services = new HashSet<string>(StringComparer.Ordinal);
        foreach (var service in config.Services)
        {
            ValidateIdentifier(service.Key.ToPascalCase());
            if (!services.Add(service.Key.ToPascalCase()))
                throw new InvalidOperationException($"Duplicate generated service '{service.Key}'.");
            foreach (var stream in Streams(service.Value))
            {
                if (string.IsNullOrWhiteSpace(stream.Name) || !IsNameToken(stream.Name))
                    throw new InvalidOperationException("Every stream requires a valid name.");
            }
            var methods = new HashSet<string>(StringComparer.Ordinal);
            foreach (var subject in Subjects(service.Value))
            {
                ValidateIdentifier(subject.Key.ToPascalCase());
                if (!methods.Add(subject.Key.ToPascalCase()))
                    throw new InvalidOperationException($"Duplicate method key '{subject.Key}' in service '{service.Key}'.");
                var binding = subject.Binding;
                if (binding is null)
                    throw new InvalidOperationException($"Missing subject binding for '{subject.Key}'.");
                if (string.IsNullOrWhiteSpace(binding.Subject) || binding.Subject.Split('.').Any(t => !SubjectToken(t)))
                    throw new InvalidOperationException($"Invalid literal subject '{binding.Subject}'.");
                if (!subjects.Add(binding.Subject))
                    throw new InvalidOperationException($"Duplicate subject/model binding for '{binding.Subject}'.");
                ValidateModel(binding.Model);
                if (binding.ReplyModel is not null)
                {
                    if (!subject.Core)
                        throw new InvalidOperationException($"Request/reply requires core transport for '{binding.Subject}'.");
                    ValidateModel(binding.ReplyModel);
                    if (string.IsNullOrWhiteSpace(binding.QueueGroup) || !IsNameToken(binding.QueueGroup!))
                        throw new InvalidOperationException($"Core responder '{binding.Subject}' requires a valid queue_group.");
                }
            }
            var consumers = new HashSet<string>(StringComparer.Ordinal);
            foreach (var stream in Streams(service.Value))
            foreach (var consumer in stream.Consumers)
            {
                ValidateIdentifier(consumer.DurableName.DurableNameToClassName());
                if (!IsNameToken(consumer.DurableName) ||
                    !consumers.Add(consumer.DurableName.DurableNameToClassName()))
                    throw new InvalidOperationException($"Invalid or duplicate consumer '{consumer.DurableName}'.");
                if (consumer.ForService is not null)
                    ValidateIdentifier(consumer.ForService.ToPascalCase());
                if (consumer.FilterSubjects.Count == 0 || consumer.FilterSubjects.Any(f => !ValidFilter(f)))
                    throw new InvalidOperationException($"Invalid filters for consumer '{consumer.DurableName}'.");
                var matches = Matches(stream, consumer).ToArray();
                if (matches.Length == 0)
                    throw new InvalidOperationException($"Consumer '{consumer.DurableName}' matches no declared subjects.");
                if (matches.Select(s => s.Value.Model).Distinct().Count() > 1)
                    throw new InvalidOperationException($"Consumer '{consumer.DurableName}' matches different payload types; mixed dispatch is not supported.");
            }
        }
    }

    private static bool SubjectToken(string token) => token.Length > 0 && token.All(c =>
        !char.IsWhiteSpace(c) && !char.IsControl(c) && c is not ('*' or '>' or '"' or '\\'));

    private static bool ValidFilter(string filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return false;
        var tokens = filter.Split('.');
        return tokens.Select((t, i) => t == "*" || (t == ">" && i == tokens.Length - 1) || SubjectToken(t)).All(v => v);
    }

    private static bool IsNameToken(string value) => value.All(c => char.IsLetterOrDigit(c) || c is '_' or '-');

    private static void ValidateIdentifier(string value)
    {
        if (!SyntaxFacts.IsValidIdentifier(value))
            throw new InvalidOperationException($"Invalid generated identifier '{value}'.");
    }

    private static void ValidateModel(string model)
    {
        var type = SyntaxFactory.ParseTypeName(model ?? "");
        if (string.IsNullOrWhiteSpace(model) || type.ContainsDiagnostics ||
            type is not Microsoft.CodeAnalysis.CSharp.Syntax.QualifiedNameSyntax ||
            type.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.OmittedTypeArgumentSyntax>().Any())
            throw new InvalidOperationException($"Model '{model}' must be a fully qualified C# type.");
    }

    private static IEnumerable<KeyValuePair<string, NatsContractConfig.SubjectConfig>> Matches(
        NatsContractConfig.StreamConfig stream, NatsContractConfig.ConsumerConfig consumer) =>
        stream.Subjects.Where(s => consumer.FilterSubjects.Any(f => SubjectMatches(f, s.Value.Subject)));

    private static bool SubjectMatches(string filter, string subject)
    {
        var pattern = filter.Split('.');
        var tokens = subject.Split('.');
        for (var i = 0; i < pattern.Length; i++)
        {
            if (i >= tokens.Length) return false;
            if (pattern[i] == ">") return true;
            if (pattern[i] != "*" && pattern[i] != tokens[i]) return false;
        }
        return pattern.Length == tokens.Length;
    }

    internal static string Generate(NatsContractConfig config, string ns)
    {
        var code = new StringBuilder($"""
            // <auto-generated/>
            #nullable enable
            using System;
            using System.Collections.Generic;
            using System.Runtime.CompilerServices;
            using System.Threading;
            using System.Threading.Tasks;
            using NATS.Client.Core;
            using NATS.Client.JetStream;

            namespace {ns};

            """);
        foreach (var service in config.Services)
        {
            code.AppendLine($"public sealed class {service.Key.ToPascalCase()}NatsClient(INatsConnection connection)");
            code.AppendLine("{");
            code.AppendLine("    private readonly INatsJSContext jetStream = new NatsJSContext(connection);");
            foreach (var subject in Subjects(service.Value))
            {
                var binding = subject.Binding;
                var model = "global::" + binding.Model;
                var name = subject.Key.ToPascalCase();
                var fixedSubject = config.BaseSubject + "." + binding.Subject;
                code.AppendLine($"    public async ValueTask Publish{name}Async({model} message, CancellationToken cancellationToken = default)");
                code.AppendLine("    {");
                if (subject.Core)
                    code.AppendLine($"        await connection.PublishAsync<{model}>(\"{fixedSubject}\", message, cancellationToken: cancellationToken);");
                else
                {
                    code.AppendLine($"        var ack = await jetStream.PublishAsync<{model}>(\"{fixedSubject}\", message, cancellationToken: cancellationToken);");
                    code.AppendLine("        ack.EnsureSuccess();");
                }
                code.AppendLine("    }");
                if (binding.ReplyModel is not null)
                {
                    code.AppendLine($"    public async ValueTask<NatsMsg<global::{binding.ReplyModel}>> Request{name}Async({model} message, CancellationToken cancellationToken = default)");
                    code.AppendLine("    {");
                    code.AppendLine($"        var reply = await connection.RequestAsync<{model}, global::{binding.ReplyModel}>(\"{fixedSubject}\", message, cancellationToken: cancellationToken);");
                    code.AppendLine("        if (reply.Headers is { } headers && headers.TryGetValue(\"Nats-Service-Error\", out var error))");
                    code.AppendLine($"            throw new NatsException($\"{fixedSubject} failed: {{error}}\");");
                    code.AppendLine("        return reply;");
                    code.AppendLine("    }");
                    Responder(code, binding.QueueGroup!, name, fixedSubject, model, binding.ReplyModel);
                }
            }
            foreach (var stream in Streams(service.Value))
            foreach (var consumer in stream.Consumers)
            {
                var model = "global::" + Matches(stream, consumer).First().Value.Model;
                code.AppendLine($"    public async IAsyncEnumerable<INatsJSMsg<{model}>> Read{consumer.DurableName.DurableNameToClassName()}Async([EnumeratorCancellation] CancellationToken cancellationToken = default)");
                code.AppendLine("    {");
                code.AppendLine($"        var consumer = await jetStream.GetConsumerAsync(\"{stream.Name}\", \"{consumer.DurableName}\", cancellationToken: cancellationToken);");
                code.AppendLine($"        await foreach (var message in consumer.ConsumeAsync<{model}>(cancellationToken: cancellationToken))");
                code.AppendLine("            yield return message;");
                code.AppendLine("    }");
            }
            code.AppendLine("}");
        }
        return code.ToString();
    }

    private static void Responder(StringBuilder code, string queueGroup, string name, string subject,
        string model, string replyModel)
    {
        code.AppendLine($"    public async Task Respond{name}Async(Func<{model}, CancellationToken, Task<global::{replyModel}>> handler, Action<Exception, string> onError, CancellationToken cancellationToken = default)");
        code.AppendLine("    {");
        code.AppendLine($"        await foreach (var message in connection.SubscribeAsync<{model}>(\"{subject}\", queueGroup: \"{queueGroup}\", cancellationToken: cancellationToken))");
        code.AppendLine("        {");
        code.AppendLine("            try");
        code.AppendLine("            {");
        code.AppendLine("                message.EnsureSuccess();");
        code.AppendLine("                var payload = message.Data ?? throw new InvalidOperationException(\"Request payload is empty.\");");
        code.AppendLine("                var reply = await handler(payload, cancellationToken);");
        code.AppendLine("                if (!string.IsNullOrEmpty(message.ReplyTo))");
        code.AppendLine($"                    await message.ReplyAsync<global::{replyModel}>(reply, cancellationToken: cancellationToken);");
        code.AppendLine("            }");
        code.AppendLine("            catch (Exception ex) when (ex is not OperationCanceledException)");
        code.AppendLine("            {");
        code.AppendLine("                onError(ex, message.Subject);");
        code.AppendLine("                if (!string.IsNullOrEmpty(message.ReplyTo))");
        code.AppendLine("                {");
        code.AppendLine("                    try");
        code.AppendLine("                    {");
        // Header values cannot contain line breaks.
        code.AppendLine("                        await message.ReplyAsync(headers: new NatsHeaders { [\"Nats-Service-Error\"] = ex.Message.ReplaceLineEndings(\" \"), [\"Nats-Service-Error-Code\"] = \"500\" }, cancellationToken: cancellationToken);");
        code.AppendLine("                    }");
        code.AppendLine("                    catch (Exception replyEx) when (replyEx is not OperationCanceledException)");
        code.AppendLine("                    {");
        code.AppendLine("                        onError(replyEx, message.Subject);");
        code.AppendLine("                    }");
        code.AppendLine("                }");
        code.AppendLine("            }");
        code.AppendLine("        }");
        code.AppendLine("    }");
    }
}
