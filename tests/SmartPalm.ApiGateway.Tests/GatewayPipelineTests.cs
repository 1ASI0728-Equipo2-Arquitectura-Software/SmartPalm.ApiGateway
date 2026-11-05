using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
namespace SmartPalm.ApiGateway.Tests;
public class GatewayPipelineTests
{
    private const string JwtKey = "gateway-tests-signing-key-with-at-least-32-characters";
    [Fact]
    public async Task Live_health_is_public_and_returns_correlation_id()
    {
        await using var factory = CreateFactory(); using var client = factory.CreateClient(); using var response = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.Contains("X-Correlation-Id"));
    }
    [Theory]
    [InlineData("/api/v1/users/1")]
    [InlineData("/api/v1/alerts")]
    [InlineData("/api/v1/field/visits")]
    public async Task Protected_routes_reject_requests_without_jwt(string path)
    {
        await using var factory = CreateFactory(); using var client = factory.CreateClient(); Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task Unversioned_field_routes_are_not_published()
    {
        await using var factory = CreateFactory(); using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/field/visits")).StatusCode);
    }
    private static WebApplicationFactory<Program> CreateFactory() => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
    {
        builder.UseEnvironment("Testing"); builder.UseSetting("Jwt:Issuer", "smartpalm.identity.tests"); builder.UseSetting("Jwt:Audience", "smartpalm.clients.tests"); builder.UseSetting("Jwt:Key", JwtKey);
        for (var index = 0; index < 7; index++) builder.UseSetting($"DownstreamServices:HealthUrls:{index}", null);
    });
}
