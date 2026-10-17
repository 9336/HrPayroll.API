using HrPayroll.BuildingBlocks.Hosting;
using HrPayroll.Helpdesk.Services;

// Helpdesk service: IT service requests (manager approval, then the IT desk) and the visitor desk.
var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults<HelpdeskDbContext>(HelpdeskDbContext.Layout);

builder.Services.AddScoped<Notifier>();
builder.Services.AddScoped<ItRequestService>();
builder.Services.AddScoped<VisitService>();

var app = builder.Build();
app.UseServiceDefaults();
app.Run();