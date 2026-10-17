using System.Threading.RateLimiting;
using HrPayroll.BuildingBlocks.Hosting;
using HrPayroll.BuildingBlocks.Messaging;
using HrPayroll.Contracts.Events;
using HrPayroll.Identity.Services;
using Microsoft.EntityFrameworkCore;

// Identity service: sign-in (JWT access + refresh tokens), password changes, roles & permissions.
// Owns logins and roles (published to hr.users / hr.roles); creates logins requested by the Employees service.
var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults<IdentityDbContext>(IdentityDbContext.Layout, s => s.RateLimits = o =>
    // Brute-force protection on the login endpoint: 10 attempts / minute / IP (the gateway forwards the caller's IP).
    o.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) })));

builder.Services.AddSingleton<ITokenService, TokenService>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<RoleService>();

builder.Services.AddEventHandler<LoginRequested, IdentityEventHandlers>(Topics.LoginRequests);
builder.Services.AddEventHandler<EmployeeUpserted, IdentityEventHandlers>(Topics.Employees);

// First start against an empty Kafka: publish every login and role.
builder.Services.AddSnapshot(Topics.Users, async (db, ct) =>
    (await db.Users.AsNoTracking().ToListAsync(ct)).Select(IdentityDbContext.UserEvent));
builder.Services.AddSnapshot(Topics.Roles, async (db, ct) =>
    (await db.Roles.AsNoTracking().Include(r => r.Permissions).ToListAsync(ct)).Select(IdentityDbContext.RoleEvent));

var app = builder.Build();
app.UseServiceDefaults();
app.Run();
