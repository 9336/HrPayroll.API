using HrPayroll.Attendance.Services;
using HrPayroll.BuildingBlocks.Hosting;

// Attendance service: calendar, check-in / check-out (web and mobile), work logs, team attendance, attendance areas
// (geofences), Face ID phones and the selfie face check. Keeps copies of the directory and of leave requests.
var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults<AttendanceDbContext>(AttendanceDbContext.Layout, s => s.LeaveRecords = true);

builder.Services.Configure<AttendanceOptions>(builder.Configuration.GetSection(AttendanceOptions.Section));
builder.Services.AddMemoryCache();   // one-time check-in challenges
builder.Services.AddSingleton<FaceService>();   // face models are loaded once
builder.Services.AddScoped<AttendanceFaceService>();
builder.Services.AddScoped<AttendanceLocationService>();
builder.Services.AddScoped<CalendarService>();
builder.Services.AddScoped<TeamAttendanceService>();
builder.Services.AddScoped<IdPhotoClient>();
builder.Services.AddInternalClient(builder.Configuration, IdPhotoClient.Name);

var app = builder.Build();
app.UseServiceDefaults();
app.Run();
