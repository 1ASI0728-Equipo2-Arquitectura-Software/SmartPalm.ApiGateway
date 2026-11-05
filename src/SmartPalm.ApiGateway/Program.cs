using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using SmartPalm.ApiGateway.Health;
using SmartPalm.ApiGateway.Swagger;

var builder = WebApplication.CreateBuilder(args);

var jwtIssuer = builder.Configuration["Jwt:Issuer"]
    ?? throw new InvalidOperationException("Jwt:Issuer is required.");
var jwtAudience = builder.Configuration["Jwt:Audience"]
    ?? throw new InvalidOperationException("Jwt:Audience is required.");
var jwtKey = builder.Configuration["Jwt:Key"]
    ?? throw new InvalidOperationException("Jwt:Key is required. Configure it through an environment variable.");

if (Encoding.UTF8.GetByteCount(jwtKey) < 32)
{
    throw new InvalidOperationException("Jwt:Key must contain at least 32 bytes.");
}

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtIssuer,
            ValidateAudience = true,
            ValidAudience = jwtAudience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "name",
            RoleClaimType = "role"
        };
    });

builder.Services.AddAuthorizationBuilder()
    .AddPolicy("Administrator", policy => policy.RequireRole("Administrator"));

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy => policy
        .AllowAnyOrigin()
        .AllowAnyHeader()
        .AllowAnyMethod());
});

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("gateway", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 120,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
});

builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));
builder.Services.AddHttpClient("swagger-aggregation", client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.Configure<SwaggerServicesOptions>(
    builder.Configuration.GetSection(SwaggerServicesOptions.SectionName));
builder.Services.AddSingleton<OpenApiDocumentAggregator>();
builder.Services.Configure<DownstreamServicesOptions>(builder.Configuration.GetSection(DownstreamServicesOptions.SectionName));
builder.Services.AddHealthChecks().AddCheck<DownstreamServicesHealthCheck>("downstream-services", tags: ["ready"]);

var app = builder.Build();

app.Use(async (context, next) =>
{
    const string headerName = "X-Correlation-Id";
    var correlationId = context.Request.Headers[headerName].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(correlationId))
    {
        correlationId = Guid.NewGuid().ToString("N");
        context.Request.Headers[headerName] = correlationId;
    }

    context.Response.Headers[headerName] = correlationId;
    using (app.Logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
    {
        await next();
    }
});

app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint(OpenApiDocumentAggregator.DocumentPath, "SmartPalm Platform API");
        options.EnablePersistAuthorization();
    });

    app.MapGet(OpenApiDocumentAggregator.DocumentPath, async Task<IResult> (
        OpenApiDocumentAggregator aggregator,
        CancellationToken cancellationToken) =>
    {
        try
        {
            var document = await aggregator.AggregateAsync(cancellationToken);
            return Results.Json(document);
        }
        catch (SwaggerAggregationException exception)
        {
            app.Logger.LogError(exception, "No se pudo agregar la documentación OpenAPI.");
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Swagger aggregation unavailable",
                detail: exception.Message);
        }
    }).AllowAnonymous();
}

app.MapHealthChecks("/health/live", new() { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new() { Predicate = check => check.Tags.Contains("ready") });
app.MapReverseProxy().RequireRateLimiting("gateway");

app.Run();

public partial class Program;
