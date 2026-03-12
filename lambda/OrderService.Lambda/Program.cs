using Amazon.DynamoDBv2;
using Amazon.Lambda.AspNetCoreServer.Hosting;
using Amazon.SQS;
using Asp.Versioning;
using AspNetCoreRateLimit;
using Microsoft.EntityFrameworkCore;
using OrderService.Infrastructure.Data;
using OrderService.Infrastructure.Repositories;
using OrderService.Service.Interfaces;
using OrderService.Service.Services;
using OrderProcessingSystemPOC.Shared.LambdaMigration.Shared.Idempotency;
using OrderService.Lambda.Infrastructure;
using System.Text.Json.Serialization;

// ── Lambda entry-point: ASP.NET Core hosted inside Lambda via the
//    Amazon.Lambda.AspNetCoreServer.Hosting adapter.
//    API Gateway (HTTP API, payload v2) routes all requests to this function.

var builder = WebApplication.CreateBuilder(args);

// ── Lambda hosting adapter ──────────────────────────────────────────────────
builder.Services.AddAWSLambdaHosting(LambdaEventSource.HttpApi);

// ── AWS clients ─────────────────────────────────────────────────────────────
builder.Services.AddAWSService<IAmazonDynamoDB>();
builder.Services.AddAWSService<IAmazonSQS>();

// ── Idempotency store ────────────────────────────────────────────────────────
var idempotencyTable = builder.Configuration["DynamoDB:IdempotencyTable"] ?? "order-idempotency";
builder.Services.AddSingleton<IIdempotencyStore>(sp =>
    new DynamoDbIdempotencyStore(sp.GetRequiredService<IAmazonDynamoDB>(), idempotencyTable));

// ── Rate limiting ────────────────────────────────────────────────────────────
builder.Services.AddMemoryCache();
builder.Services.Configure<IpRateLimitOptions>(builder.Configuration.GetSection("IpRateLimiting"));
builder.Services.Configure<IpRateLimitPolicies>(builder.Configuration.GetSection("IpRateLimitPolicies"));
builder.Services.AddInMemoryRateLimiting();
builder.Services.AddSingleton<IRateLimitConfiguration, RateLimitConfiguration>();

// ── API versioning ───────────────────────────────────────────────────────────
builder.Services.AddApiVersioning(options =>
{
    options.DefaultApiVersion = new ApiVersion(1, 0);
    options.AssumeDefaultVersionWhenUnspecified = true;
    options.ReportApiVersions = true;
    options.ApiVersionReader = ApiVersionReader.Combine(
        new UrlSegmentApiVersionReader(),
        new HeaderApiVersionReader("X-Api-Version"),
        new QueryStringApiVersionReader("api-version"));
}).AddApiExplorer(options =>
{
    options.GroupNameFormat = "'v'VVV";
    options.SubstituteApiVersionInUrl = true;
});

// ── MVC + Swagger ────────────────────────────────────────────────────────────
builder.Services.AddControllers().AddJsonOptions(options =>
{
    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title   = "Order Service API (Lambda)",
        Version = "v1",
        Description = "Order processing service — Lambda + SQS deployment"
    });

    // Add JWT Bearer authentication to Swagger UI so developers can paste a Cognito ID token
    var securityScheme = new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Description = "Enter JWT Bearer token obtained from Cognito (paste the 'IdToken')",
        Reference = new Microsoft.OpenApi.Models.OpenApiReference
        {
            Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
            Id = "Bearer"
        }
    };

    c.AddSecurityDefinition("Bearer", securityScheme);
    c.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
    {
        { securityScheme, new string[] { } }
    });
});

// ── Data / Repositories ──────────────────────────────────────────────────────
var ordersTableName = builder.Configuration["DynamoDB:OrdersTable"];
var processedEventsTableName = builder.Configuration["DynamoDB:IdempotencyTable"];
var connectionString = builder.Configuration.GetConnectionString("OrderDatabase");

if (!string.IsNullOrWhiteSpace(ordersTableName))
{
    builder.Services.AddScoped<IOrderRepository>(sp =>
        new DynamoDbOrderRepository(
            sp.GetRequiredService<IAmazonDynamoDB>(),
            ordersTableName,
            processedEventsTableName));
}
else if (!string.IsNullOrWhiteSpace(connectionString))
{
    builder.Services.AddDbContext<OrderDbContext>(opt => opt.UseSqlServer(connectionString));
    builder.Services.AddScoped<IOrderRepository, SqlOrderRepository>();
}
else
{
    builder.Services.AddSingleton<IOrderRepository, InMemoryOrderRepository>();
}

// ── Event publisher: SQS replaces Kafka ─────────────────────────────────────
builder.Services.AddScoped<IEventPublisher, SqsEventPublisher>();


// ── Application services ─────────────────────────────────────────────────────
builder.Services.AddScoped<IOrderService, OrderManagementService>();
builder.Services.AddHealthChecks();

var app = builder.Build();

// Quick remap: support both /swagger-init.js and /swagger/swagger-init.js
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.HasValue && ctx.Request.Path.Value.Equals("/swagger-init.js", StringComparison.OrdinalIgnoreCase))
    {
        ctx.Request.Path = "/swagger/swagger-init.js";
    }
    await next();
});

// Serve static files from wwwroot so swagger-init.js can be injected
app.UseStaticFiles();

// Optional: protect the Swagger UI with a simple token header when provided
var swaggerToken = builder.Configuration["SWAGGER_TOKEN"];
if (!string.IsNullOrWhiteSpace(swaggerToken))
{
    app.Use(async (ctx, next) =>
    {
        var path = ctx.Request.Path.Value ?? string.Empty;
        if (path.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase))
        {
            if (!ctx.Request.Headers.TryGetValue("x-swagger-token", out var val) || val != swaggerToken)
            {
                ctx.Response.StatusCode = 401;
                await ctx.Response.WriteAsync("Unauthorized");
                return;
            }
        }
        await next();
    });
}

app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "Order Service API v1");
    c.RoutePrefix = "swagger";

    // Inject external script using a relative path so it resolves correctly
    // regardless of the API Gateway stage prefix (/dev/swagger/swagger-init.js)
    c.InjectJavascript("./swagger-init.js");

});

app.UseIpRateLimiting();

app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value ?? string.Empty;
    if (!path.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase))
    {
        ctx.Response.Headers.Append("X-Content-Type-Options", "nosniff");
        ctx.Response.Headers.Append("X-Frame-Options", "DENY");
        ctx.Response.Headers.Append("Referrer-Policy", "strict-origin-when-cross-origin");
    }
    await next();
});

app.MapControllers();
app.MapHealthChecks("/health");

app.Run();
