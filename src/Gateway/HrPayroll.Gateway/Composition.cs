using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using HrPayroll.Contracts.Dtos;

namespace HrPayroll.Gateway;

/// <summary>The answer of one service: its data, or the error to return to the caller as it is.</summary>
public sealed record Part<T>(T? Value, IResult? Error)
{
    public bool Failed => Error is not null;
}

/// <summary>
/// Calls services on behalf of the caller (their access token is passed on, so every service still checks the caller's
/// own permissions) and returns the first error untouched - a 403 from a service stays a 403.
/// </summary>
public sealed class Downstream(IHttpClientFactory http, IHttpContextAccessor context, ILogger<Downstream> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public async Task<Part<T>> GetAsync<T>(string service, string path, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        var caller = context.HttpContext!;
        if (caller.Request.Headers.Authorization is { Count: > 0 } auth)
            request.Headers.Authorization = AuthenticationHeaderValue.Parse(auth.ToString());
        if (caller.Connection.RemoteIpAddress is { } ip) request.Headers.Add("X-Forwarded-For", ip.ToString());

        try
        {
            using var response = await http.CreateClient(service).SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
                return new(response.StatusCode == HttpStatusCode.NoContent ? default : await response.Content.ReadFromJsonAsync<T>(Json, ct), null);

            var body = await response.Content.ReadAsStringAsync(ct);
            var type = response.Content.Headers.ContentType?.ToString() ?? "application/problem+json";
            return new(default, Results.Content(body, type, statusCode: (int)response.StatusCode));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("The {Service} service did not answer {Path}: {Error}", service, path, ex.Message);
            return new(default, Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Service unavailable", detail: $"The {service} service is not available right now. Please try again in a moment."));
        }
    }

    public static IResult? FirstError(params object[] parts) =>
        parts.Select(p => (IResult?)p.GetType().GetProperty(nameof(Part<object>.Error))!.GetValue(p)).FirstOrDefault(e => e is not null);
}

/// <summary>Pages built from several services (API composition), with the same response models as before the split.</summary>
public static class CompositionEndpoints
{
    public static void MapComposition(this WebApplication app)
    {
        // Admin dashboard: headcount (Employees) + leave approvals (Leave) + last payroll (Payroll).
        app.MapGet("/api/dashboard/admin", async (Downstream d, CancellationToken ct) =>
        {
            var employees = d.GetAsync<EmployeeDashboardPart>("employees", "api/employees/dashboard", ct);
            var leave = d.GetAsync<LeaveAdminDashboardPart>("leave", "api/leaves/dashboard/admin", ct);
            var payroll = d.GetAsync<PayrollDashboardPart>("payroll", "api/payroll/dashboard", ct);
            await Task.WhenAll(employees, leave, payroll);
            if (Downstream.FirstError(employees.Result, leave.Result, payroll.Result) is { } error) return error;

            var (e, l, p) = (employees.Result.Value!, leave.Result.Value!, payroll.Result.Value!);
            return Results.Ok(new AdminDashboardDto(e.ActiveEmployees, l.PendingLeaveRequests, l.OnLeaveToday,
                p.LastPayrollNet, p.LastPayrollPeriod, l.RecentPendingRequests, e.HeadcountByDepartment));
        });

        // Employee dashboard: name and holidays (Employees) + balances and leaves (Leave) + latest payslip (Payroll).
        app.MapGet("/api/dashboard/employee", async (Downstream d, CancellationToken ct) =>
        {
            var me = d.GetAsync<EmployeeDto>("employees", "api/employees/me", ct);
            var leave = d.GetAsync<LeaveEmployeeDashboardPart>("leave", "api/leaves/dashboard/me", ct);
            var payslips = d.GetAsync<List<PayslipDto>>("payroll", "api/payslips/my", ct);
            var holidays = d.GetAsync<List<HolidayDto>>("employees", "api/holidays/upcoming?take=4", ct);
            await Task.WhenAll(me, leave, payslips, holidays);
            if (Downstream.FirstError(me.Result, leave.Result, payslips.Result, holidays.Result) is { } error) return error;

            var l = leave.Result.Value!;
            return Results.Ok(new EmployeeDashboardDto($"{me.Result.Value!.FirstName} {me.Result.Value.LastName}", l.Balances,
                payslips.Result.Value?.FirstOrDefault(), l.UpcomingLeaves, l.PendingRequests, holidays.Result.Value ?? []));
        });

        // Year-end page: leave year (Leave) + financial years (Payroll).
        app.MapGet("/api/year-end", async (Downstream d, TimeProvider time, CancellationToken ct) =>
        {
            var leaveYear = d.GetAsync<LeaveYearStatusDto>("leave", "api/year-end/leave-year", ct);
            var years = d.GetAsync<FinancialYearsPart>("payroll", "api/year-end/financial-years", ct);
            await Task.WhenAll(leaveYear, years);
            if (Downstream.FirstError(leaveYear.Result, years.Result) is { } error) return error;

            return Results.Ok(new YearEndOverviewDto(DateOnly.FromDateTime(time.GetLocalNow().DateTime), leaveYear.Result.Value!,
                years.Result.Value!.CurrentFinancialYear, years.Result.Value.FinancialYears));
        });
    }
}
