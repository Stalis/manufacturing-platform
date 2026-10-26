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
                retention: workqueue
                subjects:
                  start_v1: producer.tasks.start.v1
                consumers:
                  - durable_name: 'producer-worker'
                    filter_subjects:
                      - producer.tasks.start.>
              events:
                name: PRODUCTION_EVENTS
                retention: limits
                subjects:
                  started_v1: producer.events.started.v1
                  completed_v1: producer.events.completed.v1
                consumers:
                  - durable_name: 'orders-api-producer'
                    for_service: orders
                    filter_subjects:
                      - producer.events.>
          inventory:
            streams:
              commands:
                name: INVENTORY_COMMANDS
                retention: workqueue
                subjects:
                  reserve_v1: inventory.tasks.reserve.v1
                  grab_v1: inventory.tasks.grab.v1
                consumers:
                  - durable_name: 'inventory-worker'
                    filter_subjects:
                      - inventory.tasks.reserve.>
                      - inventory.tasks.grab.>
              events:
                name: INVENTORY_EVENTS
                retention: limits
                subjects:
                  delivered_v1: inventory.events.delivered.v1
                consumers:
                  - durable_name: 'producer-worker-inventory'
                    for_service: producer
                    filter_subjects:
                      - inventory.events.delivered.>
        """;

    [Fact]
    public void GeneratesNatsContractWithStreamsSubjectsAndConsumers()
    {
        var generator = new NatsSubjectsGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);

        driver = driver.AddAdditionalTexts(
            ImmutableArray.Create<AdditionalText>(
                new TestAdditionalFile("./nats.contract.yaml", SampleContractYaml))
        );

        var compilation = CSharpCompilation.Create(
            "Manufacturing.Nats.Contract",
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        driver.RunGeneratorsAndUpdateCompilation(compilation, out var newCompilation, out var diagnostics);

        Assert.Empty(diagnostics);

        var generatedTree = Assert.Single(newCompilation.SyntaxTrees, t => Path.GetFileName(t.FilePath) == "NatsContract.g.cs");
        var generatedCode = generatedTree.GetText().ToString();

        Assert.Contains("namespace Manufacturing.Nats.Contract;", generatedCode);
        Assert.Contains("public const string BaseSubject = \"manufacturing\";", generatedCode);
        Assert.Contains("public static class Producer", generatedCode);
        Assert.Contains("public const string StartV1 = \"manufacturing.producer.tasks.start.v1\";", generatedCode);
        Assert.Contains("public const string StartedV1 = \"manufacturing.producer.events.started.v1\";", generatedCode);
        Assert.Contains("public static class ProducerWorkerConsumer", generatedCode);
        Assert.Contains("public const string StreamName = \"PRODUCTION_COMMANDS\";", generatedCode);
        Assert.Contains("public const string DurableName = \"producer-worker\";", generatedCode);
        Assert.Contains("public static class ProducerWorkerInventoryConsumer", generatedCode);
        Assert.Contains("public const string StreamName = \"INVENTORY_EVENTS\";", generatedCode);
        Assert.Contains("public static class Inventory", generatedCode);
        Assert.Contains("public static class InventoryWorkerConsumer", generatedCode);
        Assert.Contains("public const string StreamName = \"INVENTORY_COMMANDS\";", generatedCode);
        Assert.Contains("public const string DurableName = \"inventory-worker\";", generatedCode);
        Assert.Contains("public static class Orders", generatedCode);
        Assert.Contains("public static class OrdersApiProducerConsumer", generatedCode);
        Assert.Contains("public const string DurableName = \"orders-api-producer\";", generatedCode);
    }
}
