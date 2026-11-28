using System.Text.Json;

namespace SmartPalm.ApiGateway.Tests;

public class GatewayConfigurationTests
{
    private readonly JsonElement _root;

    public GatewayConfigurationTests()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        _root = document.RootElement.Clone();
    }

    [Fact]
    public void Every_route_references_an_existing_cluster()
    {
        var reverseProxy = _root.GetProperty("ReverseProxy");
        var clusterIds = reverseProxy.GetProperty("Clusters")
            .EnumerateObject()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var route in reverseProxy.GetProperty("Routes").EnumerateObject())
        {
            var clusterId = route.Value.GetProperty("ClusterId").GetString()
                ?? throw new InvalidDataException($"Route '{route.Name}' has no ClusterId.");
            Assert.Contains(clusterId, clusterIds);
        }
    }

    [Fact]
    public void Authentication_route_remains_anonymous_for_existing_clients()
    {
        var route = _root.GetProperty("ReverseProxy")
            .GetProperty("Routes")
            .GetProperty("identity-authentication");

        Assert.Equal("anonymous", route.GetProperty("AuthorizationPolicy").GetString());
        Assert.Equal(
            "/api/v1/Authentication/{**catch-all}",
            route.GetProperty("Match").GetProperty("Path").GetString());
    }

    [Fact]
    public void Route_identifiers_are_unique()
    {
        var routeIds = _root.GetProperty("ReverseProxy")
            .GetProperty("Routes")
            .EnumerateObject()
            .Select(property => property.Name)
            .ToArray();

        Assert.Equal(routeIds.Length, routeIds.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Gateway_declares_all_seven_service_clusters_and_one_draft_cluster()
    {
        var actual = _root.GetProperty("ReverseProxy").GetProperty("Clusters").EnumerateObject().Select(x => x.Name).ToHashSet();
        var expected = new[] { "identity-service", "crop-service", "device-service", "ingestion-service", "alert-service", "agronomy-service", "field-service" };
        Assert.All(expected, id => Assert.Contains(id, actual));
        Assert.Equal(new[] { "field-report-draft-service" }, actual.Except(expected));
    }

    [Fact]
    public void Visit_report_draft_has_an_authenticated_POST_route_and_an_isolated_timeout()
    {
        var proxy = _root.GetProperty("ReverseProxy");
        var routes = proxy.GetProperty("Routes");
        var route = routes.GetProperty("field-visit-report-draft");
        Assert.Equal("default", route.GetProperty("AuthorizationPolicy").GetString());
        Assert.Equal(-1, route.GetProperty("Order").GetInt32());
        Assert.Equal("/api/v1/field/visits/{visitId:long}/report-draft", route.GetProperty("Match").GetProperty("Path").GetString());
        Assert.Equal(new[] { "POST" }, route.GetProperty("Match").GetProperty("Methods").EnumerateArray().Select(x => x.GetString()));
        const string draftCluster = "field-report-draft-service";
        Assert.Equal(draftCluster, route.GetProperty("ClusterId").GetString());
        Assert.Single(routes.EnumerateObject(), x => x.Value.GetProperty("ClusterId").GetString() == draftCluster);
        Assert.Equal("field-service", routes.GetProperty("field-api").GetProperty("ClusterId").GetString());
        var clusters = proxy.GetProperty("Clusters");
        Assert.Equal("00:01:00", clusters.GetProperty(draftCluster).GetProperty("HttpRequest").GetProperty("ActivityTimeout").GetString());
        Assert.Equal("00:00:10", clusters.GetProperty("field-service").GetProperty("HttpRequest").GetProperty("ActivityTimeout").GetString());
    }

    [Fact]
    public void Base_configuration_has_no_localhost_downstream_defaults()
    {
        var serialized = _root.GetRawText();

        Assert.DoesNotContain("localhost", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("host.docker.internal", serialized, StringComparison.OrdinalIgnoreCase);

        var swaggerServices = _root.GetProperty("SwaggerServices").EnumerateObject();
        Assert.All(swaggerServices, service =>
            Assert.True(string.IsNullOrEmpty(service.Value.GetString())));

        var healthUrls = _root.GetProperty("DownstreamServices")
            .GetProperty("HealthUrls").EnumerateObject();
        Assert.All(healthUrls, healthUrl =>
            Assert.True(string.IsNullOrEmpty(healthUrl.Value.GetString())));
    }

    [Theory]
    [InlineData("swagger-identity", "identity-service", "/swagger/services/identity/swagger.json")]
    [InlineData("swagger-crop", "crop-service", "/swagger/services/crop/swagger.json")]
    [InlineData("swagger-device", "device-service", "/swagger/services/device/swagger.json")]
    [InlineData("swagger-ingestion", "ingestion-service", "/swagger/services/ingestion/swagger.json")]
    [InlineData("swagger-alert", "alert-service", "/swagger/services/alert/swagger.json")]
    [InlineData("swagger-agronomy", "agronomy-service", "/swagger/services/agronomy/swagger.json")]
    [InlineData("swagger-field", "field-service", "/swagger/services/field/swagger.json")]
    public void Swagger_routes_are_public_and_proxy_the_service_document(string routeId, string clusterId, string path)
    {
        var route = _root.GetProperty("ReverseProxy").GetProperty("Routes").GetProperty(routeId);

        Assert.Equal(clusterId, route.GetProperty("ClusterId").GetString());
        Assert.Equal("anonymous", route.GetProperty("AuthorizationPolicy").GetString());
        Assert.Equal(path, route.GetProperty("Match").GetProperty("Path").GetString());
        Assert.Equal(
            "/swagger/v1/swagger.json",
            route.GetProperty("Transforms")[0].GetProperty("PathSet").GetString());
    }

    [Theory]
    [InlineData("ingestion-edge-readings", "ingestion-service", "/api/v1/edge-gateways/{gatewayMac}/sensor-readings")]
    [InlineData("device-gateways", "device-service", "/api/v1/edge-gateways/{**catch-all}")]
    [InlineData("agronomy-sector-recommendations", "agronomy-service", "/api/v1/sectors/{sectorId}/recommendations/{**catch-all}")]
    [InlineData("field-api", "field-service", "/api/v1/field/{**catch-all}")]
    [InlineData("field-sector-interventions", "field-service", "/api/v1/sectors/{sectorId}/interventions/{**catch-all}")]
    public void Overlapping_contracts_are_routed_to_their_owning_service(string routeId, string clusterId, string path)
    {
        var route = _root.GetProperty("ReverseProxy").GetProperty("Routes").GetProperty(routeId);
        Assert.Equal(clusterId, route.GetProperty("ClusterId").GetString());
        Assert.Equal(path, route.GetProperty("Match").GetProperty("Path").GetString());
    }

    [Fact]
    public void Edge_sensor_ingestion_is_anonymous_post_only_and_preserves_gateway_headers()
    {
        var route = _root.GetProperty("ReverseProxy")
            .GetProperty("Routes")
            .GetProperty("ingestion-edge-readings");

        Assert.Equal("anonymous", route.GetProperty("AuthorizationPolicy").GetString());
        Assert.Equal(
            new[] { "POST" },
            route.GetProperty("Match").GetProperty("Methods").EnumerateArray()
                .Select(method => method.GetString()).ToArray());
        Assert.True(
            _root.GetProperty("ReverseProxy").GetProperty("Clusters")
                .GetProperty("ingestion-service").GetProperty("HttpRequest")
                .GetProperty("RequestHeadersCopy").GetBoolean());
        Assert.Equal(
            "",
            _root.GetProperty("ReverseProxy").GetProperty("Clusters")
                .GetProperty("ingestion-service").GetProperty("Destinations")
                .GetProperty("primary").GetProperty("Address").GetString());
    }

    [Fact]
    public void Edge_sensor_readings_get_is_authenticated_and_uses_ingestion_service()
    {
        var route = _root.GetProperty("ReverseProxy")
            .GetProperty("Routes")
            .GetProperty("ingestion-edge-readings-get");

        Assert.Equal("ingestion-service", route.GetProperty("ClusterId").GetString());
        Assert.Equal("default", route.GetProperty("AuthorizationPolicy").GetString());
        Assert.Equal(
            "/api/v1/edge-gateways/{gatewayMac}/sensor-readings",
            route.GetProperty("Match").GetProperty("Path").GetString());
        Assert.Equal(
            new[] { "GET" },
            route.GetProperty("Match").GetProperty("Methods").EnumerateArray()
                .Select(method => method.GetString()).ToArray());
        Assert.Equal(
            "",
            _root.GetProperty("ReverseProxy").GetProperty("Clusters")
                .GetProperty(route.GetProperty("ClusterId").GetString()!)
                .GetProperty("Destinations").GetProperty("primary")
                .GetProperty("Address").GetString());
    }

    [Fact]
    public void Edge_sensor_readings_get_is_not_captured_by_the_device_gateway_catch_all()
    {
        var routes = _root.GetProperty("ReverseProxy").GetProperty("Routes");
        var getRoute = routes.GetProperty("ingestion-edge-readings-get");
        var deviceCatchAll = routes.GetProperty("device-gateways");

        Assert.Equal("GET", getRoute.GetProperty("Match").GetProperty("Methods")[0].GetString());
        Assert.Equal(
            "/api/v1/edge-gateways/{**catch-all}",
            deviceCatchAll.GetProperty("Match").GetProperty("Path").GetString());
        Assert.False(deviceCatchAll.GetProperty("Match").TryGetProperty("Methods", out _));
        Assert.NotEqual(
            deviceCatchAll.GetProperty("ClusterId").GetString(),
            getRoute.GetProperty("ClusterId").GetString());
    }

    [Fact]
    public void User_facing_routes_do_not_put_user_ids_in_the_path()
    {
        var routes = _root.GetProperty("ReverseProxy").GetProperty("Routes")
            .EnumerateObject()
            .Where(route => route.Value.GetProperty("AuthorizationPolicy").GetString() == "default");

        Assert.All(routes, route =>
        {
            var path = route.Value.GetProperty("Match").GetProperty("Path").GetString();
            Assert.NotNull(path);
            Assert.DoesNotContain("userId", path!, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Theory]
    [InlineData("identity-admin-users", "identity-service", "/api/v1/admin/users/{**catch-all}")]
    [InlineData("identity-admin-subscriptions", "identity-service", "/api/v1/admin/subscriptions/{**catch-all}")]
    [InlineData("crop-admin", "crop-service", "/api/v1/admin/plantations/{**catch-all}")]
    [InlineData("device-admin", "device-service", "/api/v1/admin/edge-gateways/{**catch-all}")]
    [InlineData("device-admin-iot", "device-service", "/api/v1/admin/iot-devices/{**catch-all}")]
    [InlineData("ingestion-admin-devices", "ingestion-service", "/api/v1/admin/devices/{**catch-all}")]
    [InlineData("agronomy-admin-projections-reconcile", "agronomy-service", "/api/v1/admin/projections/reconcile")]
    [InlineData("alert-admin", "alert-service", "/api/v1/admin/alerts/{**catch-all}")]
    public void Administrator_routes_are_scoped_to_admin_paths(
        string routeId,
        string clusterId,
        string path)
    {
        var route = _root.GetProperty("ReverseProxy").GetProperty("Routes").GetProperty(routeId);

        Assert.Equal(clusterId, route.GetProperty("ClusterId").GetString());
        Assert.Equal("Administrator", route.GetProperty("AuthorizationPolicy").GetString());
        Assert.Equal(path, route.GetProperty("Match").GetProperty("Path").GetString());
    }

    [Fact]
    public void Iot_provisioning_route_is_post_only()
    {
        var route = _root.GetProperty("ReverseProxy")
            .GetProperty("Routes")
            .GetProperty("device-admin-iot");

        Assert.Equal(
            new[] { "POST" },
            route.GetProperty("Match").GetProperty("Methods").EnumerateArray()
                .Select(method => method.GetString()).ToArray());
    }

    [Fact]
    public void Projection_reconciliation_route_is_administrator_post_only()
    {
        var route = _root.GetProperty("ReverseProxy")
            .GetProperty("Routes")
            .GetProperty("agronomy-admin-projections-reconcile");

        Assert.Equal("agronomy-service", route.GetProperty("ClusterId").GetString());
        Assert.Equal("Administrator", route.GetProperty("AuthorizationPolicy").GetString());
        Assert.Equal(
            "/api/v1/admin/projections/reconcile",
            route.GetProperty("Match").GetProperty("Path").GetString());
        Assert.Equal(
            new[] { "POST" },
            route.GetProperty("Match").GetProperty("Methods").EnumerateArray()
                .Select(method => method.GetString()).ToArray());
        Assert.Equal(
            "",
            _root.GetProperty("ReverseProxy").GetProperty("Clusters")
                .GetProperty("agronomy-service").GetProperty("Destinations")
                .GetProperty("primary").GetProperty("Address").GetString());
    }

    [Fact]
    public void Swagger_and_existing_public_routes_are_anonymous()
    {
        var anonymous = _root.GetProperty("ReverseProxy").GetProperty("Routes").EnumerateObject()
            .Where(x => x.Value.GetProperty("AuthorizationPolicy").GetString() == "anonymous").Select(x => x.Name).ToHashSet();
        Assert.Equal(new[]
        {
            "identity-authentication",
            "identity-public-plans",
            "ingestion-edge-readings",
            "swagger-identity",
            "swagger-crop",
            "swagger-device",
            "swagger-ingestion",
            "swagger-alert",
            "swagger-agronomy",
            "swagger-field"
        }.Order(), anonymous.Order());
    }

    [Fact]
    public void Agronomy_cluster_activity_timeout_allows_ai_diagnosis_requests()
    {
        var timeoutRaw = _root.GetProperty("ReverseProxy")
            .GetProperty("Clusters")
            .GetProperty("agronomy-service")
            .GetProperty("HttpRequest")
            .GetProperty("ActivityTimeout")
            .GetString();

        Assert.True(TimeSpan.TryParse(timeoutRaw, out var timeout));
        Assert.True(timeout >= TimeSpan.FromSeconds(45), $"Expected agronomy-service ActivityTimeout >= 45s, got {timeout}.");
    }
}
