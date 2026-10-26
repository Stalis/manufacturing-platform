using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Manufacturing.Nats.Contracts.Generator;

[Generator]
public class NatsSubjectsGenerator : IIncrementalGenerator
{
    private record ContractYamlData(AdditionalText Text, string TargetNamespace);
    private record LinkedConsumer(string StreamName, NatsContractConfig.ConsumerConfig Consumer);
    
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var assemblyNameProvider = context.CompilationProvider
            .Select(static (compilation, _) => compilation.AssemblyName ?? "UnknownAssembly");
        
        var provider = context.AdditionalTextsProvider
            .Where(static f => Path.GetFileName(f.Path) == "nats.contract.yaml")
            .Combine(assemblyNameProvider)
            .Combine(context.AnalyzerConfigOptionsProvider)
            .Select(static (tuple, _) =>
            {
                var additionalText = tuple.Left.Left;
                var assemblyName = tuple.Left.Right;
                var options = tuple.Right.GetOptions(additionalText);
                options.TryGetValue("build_metadata.AdditionalFiles.TargetNamespace", out var targetNamespace);
                if (string.IsNullOrWhiteSpace(targetNamespace))
                {
                    targetNamespace = assemblyName;
                }
                
                return new ContractYamlData(additionalText, targetNamespace!);
            })
            .Collect();
        
        context.RegisterSourceOutput(provider, GenerateCode);
    }
    
    private static void GenerateCode(SourceProductionContext context, ImmutableArray<ContractYamlData> files)
    {
        foreach (var (additionalText, ns) in files)
        {
            var gotText = additionalText.GetText(context.CancellationToken)?.ToString();
            if (string.IsNullOrWhiteSpace(gotText))
            {
                continue;
            }
            
            var builder = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .Build();
            var config = builder.Deserialize<NatsContractConfig>(gotText!);

            const string className = "NatsContract";

            var allConsumersList = config.Services.SelectMany(kv =>
            {
                var currentService = kv.Key;
                var commandConsumers = kv.Value.Streams.Commands.Consumers
                    .Select(c => new LinkedConsumer(kv.Value.Streams.Commands.Name, new NatsContractConfig.ConsumerConfig
                    {
                        DurableName = c.DurableName,
                        ForService = c.ForService ?? currentService,
                        FilterSubjects = c.FilterSubjects,
                    }));
                var eventConsumers = kv.Value.Streams.Events.Consumers
                    .Select(c => new LinkedConsumer(kv.Value.Streams.Events.Name, new NatsContractConfig.ConsumerConfig
                    {
                        DurableName = c.DurableName,
                        ForService = c.ForService ?? currentService,
                        FilterSubjects = c.FilterSubjects,
                    }));

                return commandConsumers.Concat(eventConsumers);
            });

            var consumersByServices = allConsumersList
                .GroupBy(v => v.Consumer.ForService!)
                .ToImmutableDictionary(g => g.Key, g => g.ToArray());

            var allServiceNames = config.Services.Keys
                .Concat(consumersByServices.Keys)
                .Distinct();
            
            var serviceClassesLines = allServiceNames
                .SelectMany(serviceName => GenerateServiceClassLines(
                    config.BaseSubject,
                    serviceName,
                    config.Services.TryGetValue(serviceName, out var service) ? service : null,
                    consumersByServices.TryGetValue(serviceName, out var consumers)
                        ? consumers
                        : []));
            
            var mainClassLines = new List<string>
            {
                $"public const string BaseSubject = \"{config.BaseSubject}\";",
            };
            mainClassLines.AddRange(serviceClassesLines);
            
            var sourceLines = new List<string>
            {
                $"namespace {ns};",
                "",
                $"public static class {className}",
                "{",
            };
            
            sourceLines.AddRange(mainClassLines.Select(v => v.Indent()));
            sourceLines.Add("}");
            sourceLines.Add("");
            
            context.AddSource($"{className}.g.cs",
                SourceText.From(string.Join("\n", [.. sourceLines]), Encoding.UTF8));
        }
    }

    private static IEnumerable<string> GenerateServiceClassLines(
        string baseSubject,
        string serviceName,
        NatsContractConfig.Service? service,
        IEnumerable<LinkedConsumer> linkedConsumers)
    {
        var result = new List<string>
        {
            $"public static class {serviceName.ToPascalCase()}",
            "{",
        };

        if (service is not null)
        {
            var commandStreamLines = GenerateStreamLines(baseSubject, service.Streams.Commands);
            var eventStreamLines = GenerateStreamLines(baseSubject, service.Streams.Events);

            var commandStreamClass = new List<string>
            {
                "public static class CommandStream",
                "{",
            };
            commandStreamClass.AddRange(commandStreamLines.Select(v => v.Indent()));
            commandStreamClass.Add("}");
            
            var eventStreamClass = new List<string>
            {
                "public static class EventStream",
                "{",
            };
            eventStreamClass.AddRange(eventStreamLines.Select(v => v.Indent()));
            eventStreamClass.Add("}");

            result.AddRange(commandStreamClass.Select(v => v.Indent()));
            result.AddRange(eventStreamClass.Select(v => v.Indent()));
        }

        result.AddRange(linkedConsumers
            .Select(v => GenerateConsumerClassLines(baseSubject, v))
            .SelectMany(v => v)
            .Select(v => v.Indent()));
        result.Add("}");

        return result;
    }

    private static IEnumerable<string> GenerateStreamLines(string baseSubject, NatsContractConfig.StreamConfig streamConfig)
    {
        var result = new List<string>
        {
            $"""public const string Name = "{streamConfig.Name}";""",
            "public static class Subjects",
            "{",
        };
        var subjects = streamConfig.Subjects
            .Select(kv => $"""public const string {kv.Key.ToPascalCase()} = "{baseSubject}.{kv.Value}";""")
            .Select(v => v.Indent(4));
        
        result.AddRange(subjects);
        result.Add("}");
        
        return result;
    }

    private static IEnumerable<string> GenerateConsumerClassLines(string baseSubject, LinkedConsumer linkedConsumer)
    {
        var consumer = linkedConsumer.Consumer;
        var baseName = consumer.DurableName.DurableNameToClassName();
        var consumerClassName = baseName.EndsWith("Consumer", StringComparison.Ordinal)
            ? baseName
            : $"{baseName}Consumer";

        var filterSubjects = consumer.FilterSubjects
            .Select(s => $"\"{baseSubject}.{s}\"");

        var result = new List<string>
        {
            $"public static class {consumerClassName}",
            "{",
            $"""public const string StreamName = "{linkedConsumer.StreamName}";""".Indent(),
            $"""public const string DurableName = "{consumer.DurableName}";""".Indent(),
            $"public static readonly string[] FilterSubjects = [{string.Join(", ", filterSubjects)}];".Indent(),
            "}",
        };
        return result;
    }
}
