using System.Security.Claims;
using HrPayroll.BuildingBlocks.Hosting;
using HrPayroll.BuildingBlocks.Messaging;
using HrPayroll.Contracts.Events;
using HrPayroll.Leave.Services;

// Leave service: leave types and policy, balances, requests and approvals (HR and reporting managers), the leave-year
// rollover. Publishes every leave request change (hr.leave-requests) for Attendance and Payroll.
var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults<LeaveDbContext>(LeaveDbContext.Layout);

builder.Services.AddScoped<WorkingDayCalculator>();
builder.Services.AddScoped<Notifier>();
builder.Services.AddScoped<LeaveService>();
builder.Services.AddScoped<LeaveDashboard>();
builder.Services.AddScoped<LeaveYearService>();

builder.Services.AddEventHandler<EmployeeUpserted, NewEmployeeLeaveAllocator>(Topics.Employees);

var app = builder.Build();
app.UseServiceDefaults();
app.Run();

namespace HrPayroll.Leave
{
    public static class ApproverExtensions
    {
        /// <summary>The caller as a leave approver (anyone's leave with leave.approve, otherwise their own team's).</summary>
        public static Approver ToApprover(this ClaimsPrincipal user) =>
            new(user.GetUserId(), user.GetEmployeeId(), user.HasPermission(Permissions.LeaveApprove));
    }
}
