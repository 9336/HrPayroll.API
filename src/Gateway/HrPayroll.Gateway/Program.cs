using HrPayroll.Gateway;

// API gateway: the only address the web and mobile apps use (same ports as the single API had: https 7169, http 5032).
//  - routes each URL to its service (YARP reverse proxy, WebSockets included for the chat hub),
//  - answers CORS for the web app,
//  - composes the dashboards and the year-end page from several services.
// Every service validates the JWT itself; the gateway only passes it on.
var builder = WebApplication.CreateBuilder(args);

var services = builder.Configuration.GetSection("Services").Get<Dictionary<string, string>>()
               ?? throw new InvalidOperationException("The Services section is missing.");

builder.Services.AddReverseProxy().LoadFromMemory(GatewayRoutes.Routes(), GatewayRoutes.Clusters(services));

foreach (var (name, address) in services)
    builder.Services.AddHttpClient(name, c =>
    {
        c.BaseAddress = new Uri(address);
        c.Timeout = TimeSpan.FromSeconds(30);
    });
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<Downstream>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddProblemDetails();

var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    // In Development, accept any localhost port (the SPA dev server may not run on 4200).
    .SetIsOriginAllowed(origin =>
        allowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase) ||
        (builder.Environment.IsDevelopment() && Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.IsLoopback))
    .AllowAnyHeader()
    .AllowAnyMethod()));

var app = builder.Build();

app.UseExceptionHandler();
if (!app.Environment.IsDevelopment()) app.UseHsts();
app.UseCors();

app.MapComposition();
app.MapReverseProxy();

app.MapGet("/health", () => Results.Ok(new { status = "Healthy", service = "gateway" }));

// Every service's /health in one place (for the run script and monitoring).
app.MapGet("/health/services", async (IHttpClientFactory http, CancellationToken ct) =>
{
    var checks = await Task.WhenAll(services.Keys.Select(async name =>
    {
        try
        {
            using var r = await http.CreateClient(name).GetAsync("health", ct);
            return (name, ok: r.IsSuccessStatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (name, ok: false);
        }
    }));
    var result = checks.ToDictionary(c => c.name, c => c.ok ? "Healthy" : "Unavailable");
    return checks.All(c => c.ok) ? Results.Ok(result) : Results.Json(result, statusCode: StatusCodes.Status503ServiceUnavailable);
});

app.Run();
