using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Manufacturing.Nats.Contracts.Generator.Tests.Utils;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Manufacturing.Nats.Contracts.Generator.Tests;

public class SourceGeneratorWithAdditionalFilesTests
{
    private const string SampleContractYaml = """
        base_subject: manufacturing
        services:
          producer:
            streams:
              commands:
                name: PRODUCTION_COMMANDS
                subjects:
                  start_v1:
                    subject: producer.tasks.start.v1
                    model: Producer.Contracts.Models.ProducerTaskV1
                consumers:
                  - durable_name: producer-worker
                    filter_subjects: [producer.tasks.start.>]
              events:
                name: PRODUCTION_EVENTS
                subjects:
                  started_v1:
                    subject: producer.events.started.v1
                    model: Producer.Contracts.Models.ProducerEventV1
                  completed_v1:
                    subject: producer.events.completed.v1
                    model: Producer.Contracts.Models.ProducerEventV1
                consumers:
                  - durable_name: orders-api-producer
                    for_service: orders
                    filter_subjects: [producer.events.>]
          inventory:
            core_subjects:
              reserve_v1:
                subject: inventory.tasks.reserve.v1
                model: Inventory.Contracts.Models.InventoryReserveTaskV1
                queue_group: inventory-worker
                reply_model: Inventory.Contracts.Models.InventoryReserveReplyV1
            streams:
              commands:
                name: INVENTORY_COMMANDS
                subjects:
                  grab_v1:
                    subject: inventory.tasks.grab.v1
                    model: Inventory.Contracts.Models.InventoryGrabTaskV1
                consumers:
                  - durable_name: inventory-worker
                    filter_subjects: [inventory.tasks.grab.>]
              events:
                name: INVENTORY_EVENTS
        """;

    private static (Compilation Compilation, GeneratorDriverRunResult Result) Generate(string yaml, string source = "")
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Concat(new[]
            {
                typeof(NATS.Client.Core.INatsConnection).Assembly.Location,
                typeof(NATS.Client.JetStream.INatsJSContext).Assembly.Location,
                typeof(Producer.Contracts.Models.ProducerTaskV1).Assembly.Location,
                typeof(Inventory.Contracts.Models.InventoryReserveTaskV1).Assembly.Location,
            }).Distinct().Select(p => MetadataReference.CreateFromFile(p));
        var compilation = CSharpCompilation.Create("Manufacturing.Nats.Contract",
            new[] { CSharpSyntaxTree.ParseText(source) }, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new NatsSubjectsGenerator());
        driver = driver.AddAdditionalTexts(ImmutableArray.Create<AdditionalText>(
            new TestAdditionalFile("./nats.contract.yaml", yaml)));
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
        return (output, driver.GetRunResult());
    }

    [Fact]
    public void GeneratedClientsCompileWithRealNatsAndContractTypes()
    {
        var (compilation, result) = Generate(SampleContractYaml, """
            using System;
            using System.Collections.Generic;
            using System.Threading;
            using System.Threading.Tasks;
            using Inventory.Contracts.Models;
            using Manufacturing.Nats.Contract;
            using NATS.Client.Core;
            using NATS.Client.JetStream;
            using Producer.Contracts.Models;
            public static class Caller
            {
                public static ValueTask Start(ProducerNatsClient client, ProducerTaskV1 message) => client.PublishStartV1Async(message);
                public static ValueTask Grab(InventoryNatsClient client, InventoryGrabTaskV1 message) => client.PublishGrabV1Async(message);
                public static ValueTask Started(ProducerNatsClient client, ProducerEventV1 message) => client.PublishStartedV1Async(message);
                public static ValueTask Completed(ProducerNatsClient client, ProducerEventV1 message) => client.PublishCompletedV1Async(message);
                public static ValueTask<NatsMsg<InventoryReserveReplyV1>> Reserve(InventoryNatsClient client, InventoryReserveTaskV1 message) => client.RequestReserveV1Async(message);
                public static IAsyncEnumerable<INatsJSMsg<ProducerEventV1>> Read(ProducerNatsClient client, CancellationToken ct) => client.ReadOrdersApiProducerAsync(ct);
                public static IAsyncEnumerable<INatsJSMsg<InventoryGrabTaskV1>> ReadGrab(InventoryNatsClient client) => client.ReadInventoryWorkerAsync();
                public static Task Respond(InventoryNatsClient client, Func<InventoryReserveTaskV1, CancellationToken, Task<InventoryReserveReplyV1>> handler, Action<Exception, string> onError) => client.RespondReserveV1Async(handler, onError);
                public const string Subject = NatsContract.Producer.CommandStream.Subjects.StartV1;
                public const string ConsumerStream = NatsContract.Orders.OrdersApiProducerConsumer.StreamName;
            }
            """);
        Assert.Empty(result.Diagnostics);
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        Assert.True(compilation.Emit(new MemoryStream()).Success);
    }

    [Fact]
    public void CoreMethodsUseExplicitServiceAndSubjectKey()
    {
        var (compilation, result) = Generate(SampleContractYaml.Replace(
            "inventory.tasks.reserve.v1", "internal.allocations.request.v2"), """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Inventory.Contracts.Models;
            using Manufacturing.Nats.Contract;
            using NATS.Client.Core;
            public static class Caller
            {
                public static ValueTask<NatsMsg<InventoryReserveReplyV1>> Reserve(InventoryNatsClient client, InventoryReserveTaskV1 message) => client.RequestReserveV1Async(message);
                public static Task Respond(InventoryNatsClient client, Func<InventoryReserveTaskV1, CancellationToken, Task<InventoryReserveReplyV1>> handler, Action<Exception, string> onError) => client.RespondReserveV1Async(handler, onError);
            }
            """);
        Assert.Empty(result.Diagnostics);
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        Assert.True(compilation.Emit(new MemoryStream()).Success);
    }

    [Fact]
    public void ClosedGenericBindingPreservesPublishAndConsumeTypes()
    {
        var (compilation, result) = Generate(SampleContractYaml.Replace(
            "Producer.Contracts.Models.ProducerTaskV1", "Example.Cell<System.String>"), """
            using System.Collections.Generic;
            using System.Threading.Tasks;
            using Manufacturing.Nats.Contract;
            using NATS.Client.JetStream;
            namespace Example
            {
                public record Cell<T>(T Value);
                public static class Caller
                {
                    public static ValueTask Send(ProducerNatsClient client) => client.PublishStartV1Async(new Cell<string>("part"));
                    public static IAsyncEnumerable<INatsJSMsg<Cell<string>>> Read(ProducerNatsClient client) => client.ReadProducerWorkerAsync();
                }
            }
            """);
        Assert.Empty(result.Diagnostics);
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        Assert.True(compilation.Emit(new MemoryStream()).Success);
    }

    [Fact]
    public void WrongPayloadCannotCompile()
    {
        var (compilation, result) = Generate(SampleContractYaml, """
            using Manufacturing.Nats.Contract;
            using Inventory.Contracts.Models;
            public static class WrongCaller
            {
                public static void Send(ProducerNatsClient client, InventoryGrabTaskV1 wrong) => client.PublishStartV1Async(wrong);
            }
            """);
        Assert.Empty(result.Diagnostics);
        Assert.Contains(compilation.GetDiagnostics(), d => d.Id == "CS1503");
    }

    [Fact]
    public void UnknownDtoCannotCompile()
    {
        var (compilation, result) = Generate(SampleContractYaml.Replace(
            "Producer.Contracts.Models.ProducerTaskV1", "Producer.Contracts.Models.DoesNotExist"));
        Assert.Empty(result.Diagnostics);
        Assert.Contains(compilation.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error && d.Id == "CS0234");
    }

    [Theory]
    [InlineData("missing-subject")]
    [InlineData("empty-subject")]
    [InlineData("invalid-subject")]
    [InlineData("missing-model")]
    [InlineData("empty-model")]
    [InlineData("empty-stream-entry")]
    [InlineData("missing-core-subject")]
    [InlineData("missing-core-model")]
    [InlineData("empty-core-subject")]
    [InlineData("empty-core-model")]
    [InlineData("duplicate-stream-subject")]
    [InlineData("duplicate-core-stream-subject")]
    [InlineData("duplicate-core-stream-key")]
    [InlineData("mixed")]
    [InlineData("stream-reply")]
    [InlineData("missing-queue")]
    [InlineData("empty-queue")]
    [InlineData("invalid-queue")]
    [InlineData("duplicate-yaml-key")]
    [InlineData("open-generic")]
    [InlineData("malformed")]
    [InlineData("empty")]
    public void InvalidContractReportsDiagnosticWithoutAnyGeneratedSource(string change)
    {
        var yaml = change switch
        {
            "missing-subject" => SampleContractYaml.Replace("            subject: producer.tasks.start.v1\n", ""),
            "empty-subject" => SampleContractYaml.Replace("subject: producer.tasks.start.v1", "subject: ''"),
            "invalid-subject" => SampleContractYaml.Replace("subject: producer.tasks.start.v1", "subject: producer.tasks.*"),
            "missing-model" => SampleContractYaml.Replace("            model: Producer.Contracts.Models.ProducerTaskV1\n", ""),
            "empty-model" => SampleContractYaml.Replace("model: Producer.Contracts.Models.ProducerTaskV1", "model: ''"),
            "empty-stream-entry" => SampleContractYaml.Replace("start_v1:\n            subject: producer.tasks.start.v1\n            model: Producer.Contracts.Models.ProducerTaskV1", "start_v1: {}"),
            "missing-core-subject" => SampleContractYaml.Replace("        subject: inventory.tasks.reserve.v1\n", ""),
            "missing-core-model" => SampleContractYaml.Replace("        model: Inventory.Contracts.Models.InventoryReserveTaskV1\n", ""),
            "empty-core-subject" => SampleContractYaml.Replace("subject: inventory.tasks.reserve.v1", "subject: ''"),
            "empty-core-model" => SampleContractYaml.Replace("model: Inventory.Contracts.Models.InventoryReserveTaskV1", "model: ''"),
            "duplicate-stream-subject" => SampleContractYaml.Replace("subject: producer.events.completed.v1", "subject: producer.events.started.v1"),
            "duplicate-core-stream-subject" => SampleContractYaml.Replace("subject: inventory.tasks.reserve.v1", "subject: producer.tasks.start.v1"),
            "duplicate-core-stream-key" => SampleContractYaml.Replace("reserve_v1:", "grab_v1:"),
            "mixed" => SampleContractYaml.Replace("subject: producer.events.completed.v1\n            model: Producer.Contracts.Models.ProducerEventV1", "subject: producer.events.completed.v1\n            model: Producer.Contracts.Models.ProducerTaskV1"),
            "stream-reply" => SampleContractYaml.Replace("            model: Producer.Contracts.Models.ProducerTaskV1", "            model: Producer.Contracts.Models.ProducerTaskV1\n            reply_model: Inventory.Contracts.Models.InventoryReserveReplyV1"),
            "missing-queue" => SampleContractYaml.Replace("        queue_group: inventory-worker\n", ""),
            "empty-queue" => SampleContractYaml.Replace("queue_group: inventory-worker", "queue_group: ''"),
            "invalid-queue" => SampleContractYaml.Replace("queue_group: inventory-worker", "queue_group: invalid queue"),
            "duplicate-yaml-key" => SampleContractYaml.Replace("            model: Producer.Contracts.Models.ProducerTaskV1", "            model: Producer.Contracts.Models.ProducerTaskV1\n            model: Producer.Contracts.Models.ProducerEventV1"),
            "open-generic" => SampleContractYaml.Replace("Producer.Contracts.Models.ProducerTaskV1", "System.Collections.Generic.List<>"),
            "malformed" => "services: [",
            "empty" => "",
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };
        var (_, result) = Generate(yaml);
        Assert.Contains(result.Diagnostics, d => d.Id == "NATS001" && d.Severity == DiagnosticSeverity.Error);
        Assert.All(result.Results, r => Assert.Empty(r.GeneratedSources));
        Assert.All(result.Results, r => Assert.Null(r.Exception));
    }
}
