using Catalog.Seed;
using Manufacturing.Catalog.Client;

try
{
    var dryRun = false;
    var productsOnly = false;
    var baseUrl = "http://localhost:5142";
    var dataPath = Path.Combine(AppContext.BaseDirectory, "catalog_seed.json");
    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--dry-run": dryRun = true; break;
            case "--products-only": productsOnly = true; break;
            case "--base-url" when i + 1 < args.Length: baseUrl = args[++i]; break;
            case "--data" when i + 1 < args.Length: dataPath = args[++i]; break;
            case "--help":
                Console.WriteLine("Catalog.Seed [--dry-run] [--products-only] [--base-url http://localhost:5142] [--data path]");
                return 0;
            default: throw new ArgumentException($"Unknown option or missing value: {args[i]}");
        }
    }
    if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https")
        || address.AbsolutePath != "/" || address.Query.Length > 0 || address.Fragment.Length > 0)
        throw new ArgumentException("--base-url must be an HTTP(S) origin, e.g. http://localhost:5142");

    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
    var seed = SeedGenerator.Generate(SeedData.Parse(await File.ReadAllTextAsync(dataPath, cancellation.Token)));
    Console.WriteLine($"Validated {seed.Materials.Length} materials and {seed.Products.Length} products.");
    using var http = new HttpClient { BaseAddress = address, Timeout = TimeSpan.FromSeconds(30) };
    await new CatalogSeeder(new CatalogClient(http), Console.Out).RunAsync(seed, productsOnly, dryRun, cancellation.Token);
    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled or timed out. Rerun to reconcile any completed requests.");
    return 1;
}
catch (Exception error)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}
