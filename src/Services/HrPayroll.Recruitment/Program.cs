using System.Threading.RateLimiting;
using HrPayroll.BuildingBlocks.Hosting;
using HrPayroll.Recruitment.Services;

// Recruitment service: jobs, candidates, hiring pipeline, interviews, recruiter activity, question banks and the
// public online-test page candidates open from their email (no login).
var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults<RecruitmentDbContext>(RecruitmentDbContext.Layout, s => s.RateLimits = o =>
    // Public candidate test page: generous for answer autosave, but stops token guessing.
    o.AddPolicy("public-test", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1) })));

builder.Services.AddScoped<RecruitmentService>();
builder.Services.AddScoped<AssessmentService>();

var app = builder.Build();
app.UseServiceDefaults();
app.Run();