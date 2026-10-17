using HrPayroll.BuildingBlocks.Hosting;
using HrPayroll.BuildingBlocks.Messaging;
using HrPayroll.Contracts.Events;
using HrPayroll.Employees.Services;
using Microsoft.EntityFrameworkCore;

// Employees service: employee records, departments, holidays, company master, org chart, ID-card photos and the
// documents employees upload. The source of the company directory every other service copies (hr.employees, ...).
var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults<EmployeesDbContext>(EmployeesDbContext.Layout);

builder.Services.AddScoped<EmployeeService>();
builder.Services.AddScoped<CompanyService>();
builder.Services.AddScoped<EmployeeUploadService>();

builder.Services.AddEventHandler<PromotionApplied, EmployeeEventHandlers>(Topics.Promotions);
builder.Services.AddEventHandler<SalaryStructureChanged, EmployeeEventHandlers>(Topics.Salaries);

// First start against an empty Kafka: publish the whole directory.
builder.Services.AddSnapshot(Topics.Departments, async (db, ct) =>
    (await db.Departments.AsNoTracking().ToListAsync(ct)).Select(EmployeesDbContext.DepartmentEvent));
builder.Services.AddSnapshot(Topics.Employees, async (db, ct) =>
    (await db.Employees.AsNoTracking().ToListAsync(ct)).Select(e => EmployeesDbContext.EmployeeEvent(e, isNew: false)));
builder.Services.AddSnapshot(Topics.Holidays, async (db, ct) =>
    (await db.Holidays.AsNoTracking().ToListAsync(ct)).Select(EmployeesDbContext.HolidayEvent));
builder.Services.AddSnapshot(Topics.Company, async (db, ct) =>
    (await db.CompanyProfiles.AsNoTracking().ToListAsync(ct)).Select(EmployeesDbContext.CompanyEvent));

var app = builder.Build();
app.UseServiceDefaults();
app.Run();
