using System.Text.Json;

namespace SmartPalm.ApiGateway.Tests;

public class ConnectivityAuthorizationTests
{
    [Fact]
    public void ConnectivityRoute_RequiresAuthentication()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "appsettings.json")));
        var route = document.RootElement.GetProperty("ReverseProxy").GetProperty("Routes").GetProperty("device-connectivity");
        Assert.Equal("default", route.GetProperty("AuthorizationPolicy").GetString());
    }
}
