using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
namespace SmartPalm.ApiGateway.Health;
public class DownstreamServicesOptions
{
    public const string SectionName = "DownstreamServices";
    public Dictionary<string, string> HealthUrls { get; set; } = [];
}
public class DownstreamServicesHealthCheck(IHttpClientFactory clients, IOptions<DownstreamServicesOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken token = default)
    {
        var failures = new List<string>(); var client = clients.CreateClient(); client.Timeout = TimeSpan.FromSeconds(3);
        foreach (var (service, url) in options.Value.HealthUrls)
        {
            try { using var response = await client.GetAsync(url, token); if (!response.IsSuccessStatusCode) failures.Add($"{service}:{(int)response.StatusCode}"); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { failures.Add($"{service}:unavailable"); }
        }
        return failures.Count == 0 ? HealthCheckResult.Healthy("All configured downstream services are reachable.") : HealthCheckResult.Unhealthy($"Unavailable downstream services: {string.Join(", ", failures)}");
    }
}
