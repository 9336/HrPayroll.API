using System.Security.Claims;
using System.Threading.RateLimiting;
using HrPayroll.BuildingBlocks.Hosting;
using HrPayroll.BuildingBlocks.Messaging;
using HrPayroll.Contracts.Events;
using Microsoft.AspNetCore.DataProtection;

// Notifications service: receives the emails every service asks for (hr.notifications.emails), keeps the email log and
// sends them through SMTP (settings page with a test email); failed emails are retried with back-off.
var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults<NotificationsDbContext>(NotificationsDbContext.Layout, s => s.RateLimits = o =>
    // Test emails go out immediately - keep the page from being used to spam.
    o.AddPolicy("email-test", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(10) })));

builder.Services.AddScoped<EmailSettingsService>();
builder.Services.AddHostedService<EmailDispatcher>();
builder.Services.AddEventHandler<EmailRequested, EmailRequestedHandler>(Topics.Emails);

// Encrypts the saved SMTP password. Keys live in the user profile (or the machine key store under IIS); the same
// application name as the original single API, so a password saved before the split can still be read.
builder.Services.AddDataProtection().SetApplicationName("HrPayroll.Api");

var app = builder.Build();
app.UseServiceDefaults();
app.Run();