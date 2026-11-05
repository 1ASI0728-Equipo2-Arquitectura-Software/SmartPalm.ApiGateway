using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace SmartPalm.ApiGateway.Swagger;

public sealed class SwaggerAggregationException : Exception
{
    public SwaggerAggregationException(string message)
        : base(message)
    {
    }

    public SwaggerAggregationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class OpenApiDocumentAggregator
{
    public const string DocumentPath = "/swagger/v1/swagger.json";

    private static readonly HashSet<string> HttpMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "get", "put", "post", "delete", "options", "head", "patch", "trace"
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SwaggerServicesOptions _options;
    private readonly ILogger<OpenApiDocumentAggregator> _logger;

    public OpenApiDocumentAggregator(
        IHttpClientFactory httpClientFactory,
        IOptions<SwaggerServicesOptions> options,
        ILogger<OpenApiDocumentAggregator> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<JsonObject> AggregateAsync(CancellationToken cancellationToken = default)
    {
        var definitions = _options.GetDefinitions();
        if (definitions.Any(definition => string.IsNullOrWhiteSpace(definition.BaseAddress)))
        {
            var missing = string.Join(", ", definitions
                .Where(definition => string.IsNullOrWhiteSpace(definition.BaseAddress))
                .Select(definition => definition.Key));
            throw new SwaggerAggregationException($"No se configuró la dirección de Swagger para: {missing}.");
        }

        var documents = await Task.WhenAll(
            definitions.Select(definition => LoadDocumentAsync(definition, cancellationToken)));

        return MergeDocuments(documents);
    }

    private async Task<LoadedSwaggerDocument> LoadDocumentAsync(
        SwaggerServiceDefinition definition,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(definition.BaseAddress, UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new SwaggerAggregationException(
                $"La dirección de Swagger del servicio {definition.Key} no es válida: '{definition.BaseAddress}'.");
        }

        var documentUri = new Uri(
            baseUri.ToString().TrimEnd('/') + "/swagger/v1/swagger.json",
            UriKind.Absolute);

        try
        {
            var client = _httpClientFactory.CreateClient("swagger-aggregation");
            using var response = await client.GetAsync(documentUri, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new SwaggerAggregationException(
                    $"El documento Swagger de {definition.Key} no está disponible " +
                    $"(HTTP {(int)response.StatusCode} {response.ReasonPhrase}).");
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            try
            {
                var document = JsonNode.Parse(content) as JsonObject
                    ?? throw new JsonException("El documento no contiene un objeto JSON raíz.");

                return new LoadedSwaggerDocument(definition, document);
            }
            catch (JsonException exception)
            {
                throw new SwaggerAggregationException(
                    $"El documento Swagger de {definition.Key} no contiene JSON válido.", exception);
            }
        }
        catch (SwaggerAggregationException)
        {
            throw;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SwaggerAggregationException(
                $"Se agotó el tiempo de espera al consultar el documento Swagger de {definition.Key}.", exception);
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning(exception, "No se pudo consultar Swagger para {Service}.", definition.Key);
            throw new SwaggerAggregationException(
                $"No se pudo consultar el documento Swagger de {definition.Key} en {documentUri}.", exception);
        }
    }

    private static JsonObject MergeDocuments(IReadOnlyCollection<LoadedSwaggerDocument> documents)
    {
        if (documents.Count == 0)
        {
            throw new SwaggerAggregationException("No hay documentos Swagger para agregar.");
        }

        var output = new JsonObject
        {
            ["openapi"] = GetRequiredString(documents.First().Document, "openapi", "el primer documento"),
            ["info"] = new JsonObject
            {
                ["title"] = "SmartPalm Platform API",
                ["version"] = "v1",
                ["description"] = "Documento OpenAPI agregado de los microservicios de SmartPalm."
            },
            ["servers"] = new JsonArray(new JsonObject { ["url"] = "/" }),
            ["tags"] = new JsonArray(),
            ["paths"] = new JsonObject(),
            ["components"] = new JsonObject(),
            ["security"] = new JsonArray(new JsonObject { ["Bearer"] = new JsonArray() })
        };

        var paths = output["paths"]!.AsObject();
        var tags = output["tags"]!.AsArray();
        var components = output["components"]!.AsObject();
        var knownTags = new HashSet<string>(StringComparer.Ordinal);
        var operationIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var loadedDocument in documents)
        {
            var definition = loadedDocument.Definition;
            var source = loadedDocument.Document;

            AddTags(source, definition, tags, knownTags);
            AddPaths(source, definition, paths, knownTags, tags, operationIds);
            AddComponents(source, definition, components);
        }

        EnsureBearerScheme(components);
        return output;
    }

    private static void AddTags(
        JsonObject source,
        SwaggerServiceDefinition definition,
        JsonArray outputTags,
        HashSet<string> knownTags)
    {
        if (source["tags"] is not JsonArray sourceTags)
        {
            return;
        }

        foreach (var sourceTag in sourceTags.OfType<JsonObject>())
        {
            var originalName = GetRequiredString(sourceTag, "name", $"la etiqueta de {definition.Key}");
            AddTag(outputTags, knownTags, definition, originalName, sourceTag);
        }
    }

    private static void AddPaths(
        JsonObject source,
        SwaggerServiceDefinition definition,
        JsonObject outputPaths,
        HashSet<string> knownTags,
        JsonArray outputTags,
        HashSet<string> operationIds)
    {
        if (source["paths"] is not JsonObject sourcePaths)
        {
            throw new SwaggerAggregationException($"El documento Swagger de {definition.Key} no contiene 'paths'.");
        }

        foreach (var sourcePath in sourcePaths)
        {
            if (outputPaths.ContainsKey(sourcePath.Key))
            {
                throw new SwaggerAggregationException(
                    $"Se detectó una ruta duplicada en Swagger: '{sourcePath.Key}' " +
                    $"(servicio {definition.Key}).");
            }

            if (sourcePath.Value is not JsonObject sourcePathItem)
            {
                throw new SwaggerAggregationException(
                    $"La ruta '{sourcePath.Key}' del servicio {definition.Key} no es un objeto válido.");
            }

            var pathItem = (JsonObject)sourcePathItem.DeepClone();
            var operationCount = 0;
            foreach (var operation in pathItem.ToArray())
            {
                if (!HttpMethods.Contains(operation.Key) || operation.Value is not JsonObject operationObject)
                {
                    continue;
                }

                operationCount++;
                ValidateOperationId(definition, sourcePath.Key, operation.Key, operationObject, operationIds);
                PrefixOperationTags(definition, operationObject, knownTags, outputTags);
            }

            if (operationCount == 0)
            {
                AddTag(outputTags, knownTags, definition, definition.DisplayName, null);
            }

            outputPaths[sourcePath.Key] = pathItem;
        }
    }

    private static void ValidateOperationId(
        SwaggerServiceDefinition definition,
        string path,
        string method,
        JsonObject operation,
        HashSet<string> operationIds)
    {
        if (operation["operationId"] is not JsonValue operationIdValue
            || !operationIdValue.TryGetValue<string>(out var operationId)
            || string.IsNullOrWhiteSpace(operationId))
        {
            return;
        }

        if (!operationIds.Add(operationId))
        {
            throw new SwaggerAggregationException(
                $"Se detectó un operationId duplicado en Swagger: '{operationId}' " +
                $"(servicio {definition.Key}, {method.ToUpperInvariant()} {path}).");
        }
    }

    private static void PrefixOperationTags(
        SwaggerServiceDefinition definition,
        JsonObject operation,
        HashSet<string> knownTags,
        JsonArray outputTags)
    {
        if (operation["tags"] is not JsonArray sourceTags || sourceTags.Count == 0)
        {
            AddTag(outputTags, knownTags, definition, definition.DisplayName, null);
            operation["tags"] = new JsonArray(PrefixedTag(definition, definition.DisplayName));
            return;
        }

        var prefixedTags = new JsonArray();
        foreach (var sourceTag in sourceTags)
        {
            if (sourceTag is not JsonValue tagValue || !tagValue.TryGetValue<string>(out var originalName))
            {
                throw new SwaggerAggregationException(
                    $"Una operación del servicio {definition.Key} contiene una etiqueta inválida.");
            }

            AddTag(outputTags, knownTags, definition, originalName, null);
            prefixedTags.Add(PrefixedTag(definition, originalName));
        }

        operation["tags"] = prefixedTags;
    }

    private static void AddTag(
        JsonArray outputTags,
        HashSet<string> knownTags,
        SwaggerServiceDefinition definition,
        string originalName,
        JsonObject? sourceTag)
    {
        var prefixedName = PrefixedTag(definition, originalName);
        if (!knownTags.Add(prefixedName))
        {
            return;
        }

        var tag = sourceTag is null
            ? new JsonObject()
            : (JsonObject)sourceTag.DeepClone();
        tag["name"] = prefixedName;
        outputTags.Add(tag);
    }

    private static void AddComponents(
        JsonObject source,
        SwaggerServiceDefinition definition,
        JsonObject outputComponents)
    {
        if (source["components"] is not JsonObject sourceComponents)
        {
            return;
        }

        foreach (var section in sourceComponents)
        {
            if (section.Value is not JsonObject sourceEntries)
            {
                throw new SwaggerAggregationException(
                    $"La sección components.{section.Key} del servicio {definition.Key} no es válida.");
            }

            if (outputComponents[section.Key] is not JsonObject outputEntries)
            {
                outputEntries = new JsonObject();
                outputComponents[section.Key] = outputEntries;
            }

            foreach (var entry in sourceEntries)
            {
                if (outputEntries[entry.Key] is not null)
                {
                    if (section.Key.Equals("securitySchemes", StringComparison.Ordinal)
                        && JsonNode.DeepEquals(outputEntries[entry.Key], entry.Value))
                    {
                        continue;
                    }

                    throw new SwaggerAggregationException(
                        $"Se detectó un componente duplicado en Swagger: " +
                        $"components.{section.Key}.{entry.Key} (servicio {definition.Key}).");
                }

                outputEntries[entry.Key] = entry.Value?.DeepClone();
            }
        }
    }

    private static void EnsureBearerScheme(JsonObject components)
    {
        if (components["securitySchemes"] is not JsonObject securitySchemes)
        {
            securitySchemes = new JsonObject();
            components["securitySchemes"] = securitySchemes;
        }

        if (securitySchemes["Bearer"] is null)
        {
            securitySchemes["Bearer"] = new JsonObject
            {
                ["description"] = "Pega solo el token JWT; Swagger agregará Bearer automáticamente.",
                ["name"] = "Authorization",
                ["in"] = "header",
                ["scheme"] = "bearer",
                ["bearerFormat"] = "JWT",
                ["type"] = "http"
            };
        }
    }

    private static string PrefixedTag(SwaggerServiceDefinition definition, string tagName) =>
        $"{definition.DisplayName} / {tagName}";

    private static string GetRequiredString(JsonObject source, string propertyName, string context)
    {
        if (source[propertyName] is JsonValue value
            && value.TryGetValue<string>(out var result)
            && !string.IsNullOrWhiteSpace(result))
        {
            return result;
        }

        throw new SwaggerAggregationException($"Falta '{propertyName}' en {context}.");
    }

    private sealed record LoadedSwaggerDocument(
        SwaggerServiceDefinition Definition,
        JsonObject Document);
}
