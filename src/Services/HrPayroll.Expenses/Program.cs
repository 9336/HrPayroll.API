using HrPayroll.BuildingBlocks.Hosting;
using HrPayroll.Expenses.Services;

// Expenses service: the office expense register (petty cash) and employees' travel claims
// (reporting manager -> admin approval -> paid).
var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults<ExpensesDbContext>(ExpensesDbContext.Layout);

builder.Services.AddScoped<ExpenseService>();
builder.Services.AddScoped<TravelClaimService>();

var app = builder.Build();
app.UseServiceDefaults();
app.Run();