using System.Threading.RateLimiting;
using HealthChecks.UI.Client;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.ResponseCompression;
using Serilog;
using Serilog.Events;
using Tenantix.Application;
using Tenantix.Infrastructure;
using Tenantix_WebApi.Middlewares;

// =====================
// Bootstrap Serilog (early init — catches startup errors)
// =====================
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    Log.Information("Starting Tenantix Platform API...");

    var builder = WebApplication.CreateBuilder(args);

    // =====================
    // Port configuration (Render injects PORT env var)
    // =====================
    var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

    // =====================
    // Serilog (full configuration from appsettings)
    // =====================
    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .Enrich.WithEnvironmentName()
        .Enrich.WithMachineName()
        .Enrich.WithThreadId()
        .WriteTo.Console(outputTemplate:
            "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}{NewLine}  {Message:lj}{NewLine}{Exception}"));

    // =====================
    // Add services
    // =====================

    // Controllers
    builder.Services.AddControllers();

    // Application (MediatR, FluentValidation, Application layer)
    builder.Services.AddApplication();

    // Infrastructure (DbContext, Identity, Finbuckle, Repos, etc.)
    builder.Services.AddInfrastructureServices(builder.Configuration);

    // JWT Authentication
    builder.Services.AddJwtAuthentication(
        builder.Services.GetJwtSettings(builder.Configuration)
    );

    // =====================
    // Health Checks (PostgreSQL)
    // =====================
    builder.Services.AddHealthChecks()
        .AddNpgSql(
            builder.Configuration.GetConnectionString("DefaultConnection")!,
            name: "postgresql",
            tags: new[] { "db", "ready" },
            timeout: TimeSpan.FromSeconds(5));

    // =====================
    // CORS
    // =====================
    var allowedOrigins = builder.Configuration
        .GetSection("CorsSettings:AllowedOrigins")
        .Get<string[]>() ?? Array.Empty<string>();

    builder.Services.AddCors(options =>
    {
        options.AddPolicy("TenantixCorsPolicy", policy =>
        {
            if (allowedOrigins.Length > 0)
            {
                policy.WithOrigins(allowedOrigins)
                    .AllowAnyHeader()
                    .AllowAnyMethod()
                    .AllowCredentials();
            }
            else
            {
                // Fallback: no origins allowed (secure default)
                policy.WithOrigins("https://localhost")
                    .AllowAnyHeader()
                    .AllowAnyMethod();
            }
        });
    });

    // =====================
    // Rate Limiting (.NET 8 built-in)
    // =====================
    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

        // Global: 100 requests per minute per IP
        options.AddPolicy("fixed", httpContext =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 100,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0
                }));

        // Auth endpoints: 10 requests per minute per IP
        options.AddPolicy("auth", httpContext =>
            RateLimitPartition.GetSlidingWindowLimiter(
                partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                factory: _ => new SlidingWindowRateLimiterOptions
                {
                    PermitLimit = 10,
                    Window = TimeSpan.FromMinutes(1),
                    SegmentsPerWindow = 4,
                    QueueLimit = 0
                }));
    });

    // =====================
    // Response Compression
    // =====================
    builder.Services.AddResponseCompression(options =>
    {
        options.EnableForHttps = true;
        options.Providers.Add<BrotliCompressionProvider>();
        options.Providers.Add<GzipCompressionProvider>();
    });

    var app = builder.Build();

    // =====================
    // Initialize databases (skip in Test environment)
    // =====================
    if (!app.Environment.IsEnvironment("Test"))
    {
        await app.Services.AddDatabaseInitializerAsync();
    }

    // =====================
    // Middleware pipeline (order matters!)
    // =====================

    // 1. Serilog request logging (first — captures all requests)
    app.UseSerilogRequestLogging(options =>
    {
        options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
        {
            diagnosticContext.Set("RequestHost", httpContext.Request.Host.Value);
            diagnosticContext.Set("UserAgent", httpContext.Request.Headers.UserAgent.ToString());
        };
    });

    // 2. Response compression
    app.UseResponseCompression();

    // 3. Global error handling
    app.UseMiddleware<ErrorHandlingMiddleware>();

    // 4. CORS (must be before auth)
    app.UseCors("TenantixCorsPolicy");

    // 5. Rate limiting
    app.UseRateLimiter();

    // 6. Infrastructure middleware (Multi-Tenant, Auth, Swagger)
    app.UseInfrastructure();

    // 7. Map endpoints
    app.MapControllers();

    // 8. Health check endpoints
    app.MapHealthChecks("/healthz", new HealthCheckOptions
    {
        Predicate = _ => false, // Liveness — no dependency checks
        ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse
    });

    app.MapHealthChecks("/ready", new HealthCheckOptions
    {
        Predicate = check => check.Tags.Contains("ready"),
        ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse
    });

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}

public partial class Program { }
