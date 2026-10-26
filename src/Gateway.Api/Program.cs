using Gateway.Api;
using Gateway.Api.Clients;
using Gateway.Api.GraphQL;
using Manufacturing.Catalog.Client;
using NATS.Client.Core;
using NATS.Client.Hosting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

var catalogBaseUrl = builder.Configuration["CatalogApi:BaseUrl"] ?? "http://localhost:5142";
builder.Services.AddHttpClient<ICatalogClient, CatalogClient>(client =>
{
    client.BaseAddress = new Uri(catalogBaseUrl);
});

var ordersBaseUrl = builder.Configuration["OrdersApi:BaseUrl"] ?? "http://localhost:5139";
builder.Services.AddHttpClient<IOrdersClient, OrdersClient>(client =>
{
    client.BaseAddress = new Uri(ordersBaseUrl);
});

builder.Services.AddNats(configureOpts: options =>
    options with
    {
        Url = builder.Configuration["NATS:Url"] ?? NatsOpts.Default.Url,
        Name = "Manufacturing.Gateway.Api",
        SerializerRegistry = new NatsJsonContextSerializerRegistry(
            ProducerEventV1JsonContext.Default
        ),
    });

builder.Services.AddHostedService<NatsSubscriptionBridge>();

builder.Services
    .AddGraphQLServer()
    .AddQueryType<Query>()
    .AddMutationType<Mutation>()
    .AddSubscriptionType<Subscription>()
    .AddInMemorySubscriptions();

var app = builder.Build();

app.UseCors();
app.UseWebSockets();

app.MapGraphQL("/graphql");

app.Run();
