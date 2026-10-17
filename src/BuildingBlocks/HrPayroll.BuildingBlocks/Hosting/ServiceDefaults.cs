using System.Security.Claims;
using System.Text;
using System.Text.Json.Serialization;
using HrPayroll.BuildingBlocks.Auth;
using HrPayroll.BuildingBlocks.Common;
using HrPayroll.BuildingBlocks.Data;
using HrPayroll.BuildingBlocks.Directory;
using HrPayroll.BuildingBlocks.Email;
using HrPayroll.BuildingBlocks.Messaging;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;

namespace HrPayroll.BuildingBlocks.Hosting;

/// <summary>Per-service choices for <see cref="ServiceDefaults.AddServiceDefaults{TDb}"/>.</summary>
public sealed class ServiceSetup
{
    /// <summary>Path whose WebSocket requests may carry the token as ?access_token= (SignalR hubs).</summary>
    public string? HubPath { get; set; }
    /// <summary>Keep a copy of leave requests (Attendance, Payroll).</summary>
    public bool LeaveRecords { get; set; }
    /// <summary>Extra rate-limiting policies.</summary>
    public Action<RateLimiterOptions>? RateLimits { get; set; }
}

public static class ServiceDefaults
{
    /// <summary>
    /// Everything a service needs: its database (connection string "Db"), JWT validation and permission policies,
    /// ProblemDetails errors, controllers, Swagger, the Kafka outbox relay and consumer, the copy of the company
    /// directory, the email queue and /health.
    /// </summary>
    public static WebApplicationBuilder AddServiceDefaults<TDb>(this WebApplicationBuilder builder, DirectoryModel directory,
        Action<ServiceSetup>? configure = null)
        where TDb : ServiceDbContext
    {
        var setup = new ServiceSetup();
        configure?.Invoke(setup);
        var services = builder.Services;
        var config = builder.Configuration;

        // ---------------------------------------------------------------- Database (this service's own)
        services.AddDbContext<TDb>(o => o.UseSqlServer(config.GetConnectionString("Db"), sql => sql.UseCompatibilityLevel(130)));
        services.AddScoped<ServiceDbContext>(sp => sp.GetRequiredService<TDb>());

        // ---------------------------------------------------------------- JWT (tokens are issued by the Identity service)
        services.AddOptions<JwtOptions>().Bind(config.GetSection(JwtOptions.Section))
            .Validate(o => o.SigningKey?.Length >= 32, "Jwt:SigningKey must be at least 32 characters.").ValidateOnStart();
        var jwt = config.GetSection(JwtOptions.Section).Get<JwtOptions>() ?? new JwtOptions();
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
        {
            o.MapInboundClaims = false;
            o.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = jwt.Issuer,
                ValidateAudience = true,
                ValidAudience = jwt.Audience,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey ?? string.Empty)),
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30),
                NameClaimType = ClaimTypes.Name,
                RoleClaimType = ClaimTypes.Role,
            };
            if (setup.HubPath is string hub)
            {
                // WebSockets cannot send an Authorization header: SignalR passes the token as ?access_token=.
                o.Events = new JwtBearerEvents
                {
                    OnMessageReceived = ctx =>
                    {
                        var token = ctx.Request.Query["access_token"];
                        if (!string.IsNullOrEmpty(token) && ctx.HttpContext.Request.Path.StartsWithSegments(hub)) ctx.Token = token;
                        return Task.CompletedTask;
                    },
                };
            }
        });
        services.AddAuthorization();
        services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationPolicyProvider, PermissionPolicyProvider>();

        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            setup.RateLimits?.Invoke(o);
        });

        // ---------------------------------------------------------------- Common services
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<OrgService>();
        services.Configure<EmailOptions>(config.GetSection(EmailOptions.Section));
        services.AddScoped<EmailQueue>();
        services.Configure<InternalApiOptions>(config.GetSection(InternalApiOptions.Section));

        // ---------------------------------------------------------------- Kafka: outbox relay + consumer + directory copy
        services.Configure<KafkaOptions>(config.GetSection(KafkaOptions.Section));
        services.AddSingleton<OutboxSignal>();
        services.AddSingleton<KafkaTopics>();
        EventSubscriptionExtensions.Subscriptions(services);
        services.AddDirectoryCopy(directory, setup.LeaveRecords);
        services.AddHostedService<OutboxRelay>();
        services.AddHostedService<KafkaConsumerService>();

        // ---------------------------------------------------------------- HTTP
        services.AddProblemDetails();
        services.AddExceptionHandler<GlobalExceptionHandler>();
        services.AddControllers().AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        services.Configure<ForwardedHeadersOptions>(o =>
        {
            // Requests arrive through the gateway: use the caller's real address (login rate limit, audit).
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
        });

        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(c =>
        {
            c.SwaggerDoc("v1", new OpenApiInfo { Title = builder.Environment.ApplicationName, Version = "v1" });
            var scheme = new OpenApiSecurityScheme
            {
                Name = "Authorization",
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                In = ParameterLocation.Header,
                Description = "Paste the access token returned by POST /api/auth/login",
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" },
            };
            c.AddSecurityDefinition("Bearer", scheme);
            c.AddSecurityRequirement(new OpenApiSecurityRequirement { [scheme] = [] });
            c.CustomSchemaIds(t => t.FullName?.Replace("+", "."));
        });
        return builder;
    }

    public static WebApplication UseServiceDefaults(this WebApplication app)
    {
        app.UseForwardedHeaders();
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI();
        }
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        app.MapGet("/health", () => Results.Ok(new { status = "Healthy", service = app.Environment.ApplicationName })).AllowAnonymous();
        return app;
    }
}
