using HrPayroll.BuildingBlocks.Hosting;
using HrPayroll.BuildingBlocks.Messaging;
using HrPayroll.Contracts.Events;
using HrPayroll.Payroll.Services;
using Microsoft.EntityFrameworkCore;

// Payroll service: salary structures, monthly payroll and payslips, offer / relieving / promotion letters and Form 16,
// promotions (sent to the Employees service as hr.promotions events) and financial-year closing.
// Publishes salary revisions (hr.salaries); keeps copies of the directory and of leave requests (unpaid leave).
var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults<PayrollDbContext>(PayrollDbContext.Layout, s => s.LeaveRecords = true);

builder.Services.AddScoped<WorkingDayCalculator>();
builder.Services.AddScoped<Notifier>();
builder.Services.AddScoped<SalaryService>();
builder.Services.AddScoped<PayrollService>();
builder.Services.AddScoped<DocumentService>();
builder.Services.AddScoped<PromotionService>();
builder.Services.AddScoped<FinancialYearService>();
builder.Services.AddHostedService<PromotionScheduler>();

// First start against an empty Kafka: publish every salary revision (the Employees service shows current pay).
builder.Services.AddSnapshot(Topics.Salaries, async (db, ct) =>
    (await ((PayrollDbContext)db).SalaryStructures.AsNoTracking().ToListAsync(ct)).Select(s => PayrollDbContext.SalaryEvent(s, deleted: false)));

var app = builder.Build();
app.UseServiceDefaults();
app.Run();
