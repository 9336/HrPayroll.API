using HrPayroll.BuildingBlocks.Hosting;
using HrPayroll.Performance.Services;

// Performance service: appraisal cycles (self-assessment + manager review), appreciations / complaints about employees,
// and daily tasks team leads assign to their team.
var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults<PerformanceDbContext>(PerformanceDbContext.Layout);

builder.Services.AddScoped<AppraisalService>();
builder.Services.AddScoped<FeedbackService>();
builder.Services.AddScoped<TaskService>();

var app = builder.Build();
app.UseServiceDefaults();
app.Run();