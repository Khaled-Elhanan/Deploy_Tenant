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
    // Database connection string (Render provides DATABASE_URL in postgres:// URI format)
    // Convert to Npgsql ADO.NET format if DATABASE_URL is present
    // =====================
    var databaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL");
    if (!string.IsNullOrEmpty(databaseUrl))
    {
        var connStr = ConvertPostgresUriToConnectionString(databaseUrl);
        builder.Configuration["ConnectionStrings:DefaultConnection"] = connStr;
        Log.Information("Database connection configured from DATABASE_URL");
    }

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
    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
    if (!string.IsNullOrEmpty(connectionString))
    {
        builder.Services.AddHealthChecks()
            .AddNpgSql(
                connectionString,
                name: "postgresql",
                tags: new[] { "db", "ready" },
                timeout: TimeSpan.FromSeconds(5));
    }
    else
    {
        builder.Services.AddHealthChecks();
    }

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
                // Fallback: allow any origin in production for API-only backend
                policy.AllowAnyOrigin()
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

    Exception? startupException = null;

    // =====================
    // Initialize databases (skip in Test environment)
    // =====================
    if (!app.Environment.IsEnvironment("Test"))
    {
        try
        {
            await app.Services.AddDatabaseInitializerAsync();
        }
        catch (Exception ex)
        {
            startupException = ex;
            Log.Error(ex, "Database initialization failed — the app will start but DB operations may fail");
        }
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

    app.MapGet("/db-status", () => startupException == null 
        ? Results.Ok("Database initialization successful.") 
        : Results.Problem(startupException.ToString()));

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

// =====================
// Helper: Convert postgres:// URI to Npgsql connection string
// Render provides: postgres://user:password@host:port/database
// Npgsql needs:    Host=...;Port=...;Database=...;Username=...;Password=...
// =====================
static string ConvertPostgresUriToConnectionString(string databaseUrl)
{
    var uri = new Uri(databaseUrl);
    var userInfo = uri.UserInfo.Split(':');
    var username = userInfo[0];
    var password = userInfo.Length > 1 ? userInfo[1] : "";
    var host = uri.Host;
    var port = uri.Port > 0 ? uri.Port : 5432;
    var database = uri.AbsolutePath.TrimStart('/');

    return $"Host={host};Port={port};Database={database};Username={username};Password={password};SSL Mode=Require;Trust Server Certificate=true";
}

public partial class Program { }
