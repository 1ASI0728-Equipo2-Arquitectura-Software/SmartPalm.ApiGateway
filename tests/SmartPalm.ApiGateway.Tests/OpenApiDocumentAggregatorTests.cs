using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmartPalm.ApiGateway.Swagger;

namespace SmartPalm.ApiGateway.Tests;

public sealed class OpenApiDocumentAggregatorTests
{
    [Fact]
    public async Task AggregateAsync_merges_all_seven_documents_and_configures_bearer()
    {
        var options = CreateOptions();
        var documents = CreateResponses(options, index => $"/api/v1/service-{index}");
        using var client = CreateClient(documents);
        var aggregator = CreateAggregator(options, client);

        var document = await aggregator.AggregateAsync();

        Assert.Equal(7, document["paths"]!.AsObject().Count);
        Assert.Equal("/", document["servers"]!.AsArray()[0]!["url"]!.GetValue<string>());
        Assert.Equal("SmartPalm Platform API", document["info"]!["title"]!.GetValue<string>());
        Assert.Contains(
            "Identity / Authentication",
            document["tags"]!.AsArray().Select(tag => tag!["name"]!.GetValue<string>()));

        var bearer = document["components"]!["securitySchemes"]!["Bearer"]!;
        Assert.Equal("http", bearer["type"]!.GetValue<string>());
        Assert.Equal("bearer", bearer["scheme"]!.GetValue<string>());
        Assert.Equal("JWT", bearer["bearerFormat"]!.GetValue<string>());
        Assert.Equal("Bearer", document["security"]!.AsArray()[0]!.AsObject().Single().Key);
    }

    [Fact]
    public async Task AggregateAsync_rejects_duplicate_paths()
    {
        var options = CreateOptions();
        var documents = CreateResponses(options, index => index == 1 ? "/api/v1/duplicate" : $"/api/v1/service-{index}");
        documents["identity"] = CreateDocument("/api/v1/duplicate", "IdentitySchema", "identity-operation", "Authentication");
        using var client = CreateClient(documents);
        var aggregator = CreateAggregator(options, client);

        var exception = await Assert.ThrowsAsync<SwaggerAggregationException>(() => aggregator.AggregateAsync());

        Assert.Contains("ruta duplicada", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/api/v1/duplicate", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AggregateAsync_rejects_duplicate_schema_names()
    {
        var options = CreateOptions();
        var documents = CreateResponses(options, index => $"/api/v1/service-{index}");
        documents["identity"] = CreateDocument("/api/v1/identity", "SharedSchema", "identity-operation", "Authentication");
        documents["crop"] = CreateDocument("/api/v1/crop", "SharedSchema", "crop-operation", "Plantations", "integer");
        using var client = CreateClient(documents);
        var aggregator = CreateAggregator(options, client);

        var exception = await Assert.ThrowsAsync<SwaggerAggregationException>(() => aggregator.AggregateAsync());

        Assert.Contains("components.schemas.SharedSchema", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AggregateAsync_rejects_duplicate_operation_ids()
    {
        var options = CreateOptions();
        var documents = CreateResponses(options, index => $"/api/v1/service-{index}");
        documents["identity"] = CreateDocument("/api/v1/identity", "IdentitySchema", "shared-operation", "Authentication");
        documents["crop"] = CreateDocument("/api/v1/crop", "CropSchema", "shared-operation", "Plantations");
        using var client = CreateClient(documents);
        var aggregator = CreateAggregator(options, client);

        var exception = await Assert.ThrowsAsync<SwaggerAggregationException>(() => aggregator.AggregateAsync());

        Assert.Contains("operationId duplicado", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("shared-operation", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AggregateAsync_returns_the_affected_service_when_a_document_is_unavailable()
    {
        var options = CreateOptions();
        var documents = CreateResponses(options, index => $"/api/v1/service-{index}");
        using var client = CreateClient(documents, request =>
            request.RequestUri!.Host.Equals("crop-api", StringComparison.OrdinalIgnoreCase)
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : CreateSuccessResponse(documents[request.RequestUri!.Host]));
        var aggregator = CreateAggregator(options, client);

        var exception = await Assert.ThrowsAsync<SwaggerAggregationException>(() => aggregator.AggregateAsync());

        Assert.Contains("Crop", exception.Message, StringComparison.Ordinal);
        Assert.Contains("503", exception.Message, StringComparison.Ordinal);
    }

    private static OpenApiDocumentAggregator CreateAggregator(
        SwaggerServicesOptions options,
        HttpClient client) =>
        new(
            new FixedHttpClientFactory(client),
            Options.Create(options),
            NullLogger<OpenApiDocumentAggregator>.Instance);

    private static HttpClient CreateClient(
        IReadOnlyDictionary<string, string> documents,
        Func<HttpRequestMessage, HttpResponseMessage>? responseFactory = null)
    {
        responseFactory ??= request => CreateSuccessResponse(documents[request.RequestUri!.Host]);
        return new HttpClient(new RoutingHandler(responseFactory));
    }

    private static HttpResponseMessage CreateSuccessResponse(string content) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json")
        };

    private static Dictionary<string, string> CreateResponses(
        SwaggerServicesOptions options,
        Func<int, string> pathFactory)
    {
        return options.GetDefinitions()
            .Select((definition, index) => new
            {
                Host = new Uri(definition.BaseAddress).Host,
                Content = CreateDocument(
                    pathFactory(index),
                    $"{definition.Key}Schema",
                    $"{definition.Key.ToLowerInvariant()}-operation",
                    index == 0 ? "Authentication" : definition.Key)
            })
            .ToDictionary(item => item.Host, item => item.Content, StringComparer.OrdinalIgnoreCase);
    }

    private static SwaggerServicesOptions CreateOptions() => new()
    {
        Identity = "http://identity-api",
        Crop = "http://crop-api",
        Device = "http://device-api",
        Ingestion = "http://ingestion-api",
        Alert = "http://alert-api",
        Agronomy = "http://agronomy-api",
        Field = "http://field-api"
    };

    private static string CreateDocument(
        string path,
        string schemaName,
        string operationId,
        string tag,
        string schemaType = "string")
    {
        var document = new JsonObject
        {
            ["openapi"] = "3.0.4",
            ["info"] = new JsonObject { ["title"] = "Test", ["version"] = "v1" },
            ["tags"] = new JsonArray(new JsonObject { ["name"] = tag }),
            ["paths"] = new JsonObject
            {
                [path] = new JsonObject
                {
                    ["get"] = new JsonObject
                    {
                        ["tags"] = new JsonArray(tag),
                        ["operationId"] = operationId
                    }
                }
            },
            ["components"] = new JsonObject
            {
                ["schemas"] = new JsonObject
                {
                    [schemaName] = new JsonObject { ["type"] = schemaType }
                },
                ["securitySchemes"] = new JsonObject
                {
                    ["Bearer"] = new JsonObject
                    {
                        ["description"] = "Pega solo el token JWT; Swagger agregará Bearer automáticamente.",
                        ["name"] = "Authorization",
                        ["in"] = "header",
                        ["scheme"] = "bearer",
                        ["bearerFormat"] = "JWT",
                        ["type"] = "http"
                    }
                }
            }
        };

        return document.ToJsonString();
    }

    private sealed class FixedHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
