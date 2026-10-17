using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace HrPayroll.BuildingBlocks.Auth;

/// <summary>
/// Permissions assigned to roles by an administrator (Roles &amp; Permissions page). Each module has its own
/// View / Add / Edit / Delete rights (only those that make sense for it), plus a few special actions such as
/// Approve. Codes are "module.action". Any right in a module includes its View right.
/// The built-in Admin role always has all of them. Self-service features (own leave, payslips, documents,
/// visitors...) and a manager's own reporting line need no permission.
/// </summary>
public static class Permissions
{
    public const string ClaimType = "perm";

    public const string View = "view", Add = "add", Edit = "edit", Delete = "delete", Approve = "approve", Pay = "pay";

    public const string DashboardView = "dashboard.view";

    public const string EmployeesView = "employees.view", EmployeesAdd = "employees.add", EmployeesEdit = "employees.edit", EmployeesDelete = "employees.delete";
    public const string PromotionsView = "promotions.view", PromotionsAdd = "promotions.add", PromotionsDelete = "promotions.delete";
    public const string AttendanceLocationsView = "attendancelocations.view", AttendanceLocationsAdd = "attendancelocations.add",
        AttendanceLocationsEdit = "attendancelocations.edit", AttendanceLocationsDelete = "attendancelocations.delete";

    public const string LeaveView = "leave.view", LeaveApprove = "leave.approve";
    public const string LeavePolicyEdit = "leavepolicy.edit";

    public const string SalaryView = "salary.view", SalaryAdd = "salary.add", SalaryDelete = "salary.delete";
    public const string PayrollView = "payroll.view", PayrollRun = "payroll.add", PayrollPay = "payroll.edit";
    public const string YearEndView = "yearend.view", YearEndEdit = "yearend.edit";

    public const string LettersView = "letters.view", LettersAdd = "letters.add", LettersDelete = "letters.delete";
    public const string Form16View = "form16.view", Form16Add = "form16.add", Form16Delete = "form16.delete";

    public const string ExpensesView = "expenses.view", ExpensesAdd = "expenses.add", ExpensesEdit = "expenses.edit", ExpensesDelete = "expenses.delete",
        ExpensesApprove = "expenses.approve";
    public const string TravelView = "travel.view", TravelApprove = "travel.approve", TravelPay = "travel.pay";

    public const string AppraisalsView = "appraisals.view", AppraisalsAdd = "appraisals.add", AppraisalsEdit = "appraisals.edit", AppraisalsDelete = "appraisals.delete";
    public const string FeedbackModule = "feedback";

    public const string TasksModule = "tasks";
    public const string ItView = "it.view", ItWork = "it.edit";

    public const string RecruitmentView = "recruitment.view", RecruitmentAdd = "recruitment.add", RecruitmentEdit = "recruitment.edit",
        RecruitmentDelete = "recruitment.delete";
    public const string AssessmentsModule = "assessments";

    public const string CompanyEdit = "company.edit";
    public const string VisitorsView = "visitors.view", VisitorsAdd = "visitors.add", VisitorsEdit = "visitors.edit", VisitorsDelete = "visitors.delete";
    public const string EmailLogView = "emaillog.view", EmailLogRetry = "emaillog.edit";
    public const string EmailSettingsView = "emailsettings.view", EmailSettingsEdit = "emailsettings.edit";
    public const string RolesView = "roles.view", RolesAdd = "roles.add", RolesEdit = "roles.edit", RolesDelete = "roles.delete";

    /// <summary>One right in a module: what the checkbox in that column allows.</summary>
    public record ActionDef(string Action, string Description)
    {
        public string Code(string module) => $"{module}.{Action}";
    }

    /// <summary>A row of the permission grid.</summary>
    public record Module(string Key, string Group, string Name, IReadOnlyList<ActionDef> Actions);

    private static ActionDef V(string d) => new(View, d);
    private static ActionDef A(string d) => new(Add, d);
    private static ActionDef E(string d) => new(Edit, d);
    private static ActionDef D(string d) => new(Delete, d);

    public static readonly IReadOnlyList<Module> Modules =
    [
        new("dashboard", "General", "Admin dashboard", [V("Company-wide headcount, pending approvals and payroll totals.")]),

        new("employees", "People", "Employees", [
            V("Employee directory, records, ID cards and everyone's attendance."),
            A("Add new employees (and their logins)."),
            E("Edit employee details, reporting manager, photo; reset passwords."),
            D("Deactivate or reactivate employees (and their logins).")]),
        new("promotions", "People", "Promotions", [
            V("See promotions and their history."),
            A("Promote employees and issue promotion letters (an increment also needs Salaries: Add)."),
            D("Cancel a scheduled promotion.")]),
        new("attendancelocations", "People", "Attendance areas", [
            V("See the areas (office, branch, site) employees must be inside to check in / out."),
            A("Add attendance areas on the map."),
            E("Move or resize areas, choose who they apply to, switch them on / off."),
            D("Delete attendance areas.")]),

        new("leave", "Leave", "Leave requests", [
            V("See every employee's leave requests and balances."),
            new(Approve, "Approve or reject anyone's leave (managers can always act on their own team).")]),
        new("leavepolicy", "Leave", "Leave policy", [E("Edit leave types, quotas and carry-forward rules.")]),

        new("salary", "Payroll", "Salaries", [
            V("Salary structures and history; open anyone's payslips."),
            A("Create a salary revision (increment / correction)."),
            D("Delete a salary revision.")]),
        new("payroll", "Payroll", "Payroll", [
            V("Monthly payroll summaries; open anyone's payslips."),
            A("Run (generate / regenerate) monthly payroll."),
            E("Mark a month's payroll as paid.")]),
        new("yearend", "Payroll", "Year-end closing", [
            V("See leave rollover and financial year status."),
            E("Roll leave balances to the new year; close / reopen financial years.")]),

        new("letters", "Documents", "Offer, relieving & promotion letters", [
            V("See released letters."), A("Release offer and relieving letters."), D("Revoke letters.")]),
        new("form16", "Documents", "Form 16", [
            V("See released Form 16 (salary and tax details)."), A("Release Form 16."), D("Revoke Form 16.")]),

        new("expenses", "Expenses & travel", "Office expenses", [
            V("Expense register, receipts and reports."),
            A("Record expenses (and change own pending ones)."),
            E("Edit anyone's pending expense."),
            D("Delete anyone's pending expense."),
            new(Approve, "Approve or reject expenses and manage categories.")]),
        new("travel", "Expenses & travel", "Travel claims", [
            V("See every travel claim."),
            new(Approve, "Final approval / rejection after the reporting manager."),
            new(Pay, "Mark approved claims as paid.")]),

        new("appraisals", "Performance", "Appraisals", [
            V("Appraisal cycles, progress and every employee's appraisal."),
            A("Create appraisal cycles."),
            E("Edit and publish cycles, send reminders, reopen and review anyone's appraisal."),
            D("Delete appraisal cycles.")]),
        new(FeedbackModule, "Performance", "Appreciations & complaints", [
            V("See feedback about every employee (managers always see their reporting line)."),
            A("Record feedback about anyone."),
            E("Edit, share, resolve and attach evidence to anyone's feedback."),
            D("Delete anyone's feedback.")]),

        new(TasksModule, "Work", "Tasks", [
            V("See every task (team leads and managers always see their team's)."),
            A("Assign tasks to anyone."),
            E("Edit or reassign anyone's task."),
            D("Delete anyone's task.")]),
        new("it", "Work", "IT service desk", [
            V("See every IT request and the service desk queue."),
            E("Work on IT requests after manager approval: assign, start, complete or reject.")]),

        new("recruitment", "Recruitment", "Recruitment", [
            V("Hiring dashboard, jobs, candidates, pipeline, interviews and test results."),
            A("Add jobs, candidates and applications; schedule interviews; assign tests; log activity."),
            E("Edit jobs and candidates, move stages, reschedule / cancel interviews and tests."),
            D("Delete recruitment activity entries.")]),
        new(AssessmentsModule, "Recruitment", "Question banks", [
            V("See every question bank."),
            A("Create question banks (team leads and managers always can)."),
            E("Edit any question bank and its questions."),
            D("Delete questions from any bank.")]),

        new("company", "Administration", "Company settings", [E("Edit company details, statutory ids and logo.")]),
        new("visitors", "Administration", "Visitor desk", [
            V("See every visit."), A("Register walk-in visitors."), E("Check visitors in and out."), D("Cancel visits.")]),
        new("emaillog", "Administration", "Email log", [V("See emails sent by the system."), E("Retry failed emails.")]),
        new("emailsettings", "Administration", "Email settings", [
            V("See the outgoing mail server (SMTP) settings."), E("Change mail server settings and notifications; send test emails.")]),
        new("roles", "Administration", "Roles & permissions", [
            V("See roles, their permissions and who has which role."),
            A("Create roles."),
            E("Change roles' permissions and users' roles."),
            D("Delete roles.")]),
    ];

    public static readonly IReadOnlySet<string> Codes =
        Modules.SelectMany(m => m.Actions.Select(a => a.Code(m.Key))).ToHashSet();

    /// <summary>Adds the View right of every module in which any other right is granted.</summary>
    public static HashSet<string> Normalize(IEnumerable<string> codes)
    {
        var set = codes.Where(Codes.Contains).ToHashSet();
        foreach (var code in set.ToList())
        {
            var view = code[..code.IndexOf('.')] + "." + View;
            if (Codes.Contains(view)) set.Add(view);
        }
        return set;
    }
}

/// <summary>A role's rights in one module, e.g. for records outside the caller's own reporting line.</summary>
public readonly record struct Rights(bool View, bool Add, bool Edit, bool Delete)
{
    public static Rights Of(ClaimsPrincipal user, string module) => new(
        user.HasPermission($"{module}.{Permissions.View}"), user.HasPermission($"{module}.{Permissions.Add}"),
        user.HasPermission($"{module}.{Permissions.Edit}"), user.HasPermission($"{module}.{Permissions.Delete}"));

    public bool Any => View || Add || Edit || Delete;
}

/// <summary>Requires at least one of the listed permissions: <c>[HasPermission(Permissions.A, Permissions.B)]</c>.</summary>
public sealed class HasPermissionAttribute(params string[] permissions)
    : AuthorizeAttribute(PermissionPolicyProvider.Prefix + string.Join('|', permissions));

/// <summary>Builds "perm:a|b" policies on demand (any listed permission claim satisfies the policy).</summary>
public sealed class PermissionPolicyProvider(IOptions<AuthorizationOptions> options) : DefaultAuthorizationPolicyProvider(options)
{
    public const string Prefix = "perm:";

    public override async Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (!policyName.StartsWith(Prefix, StringComparison.Ordinal)) return await base.GetPolicyAsync(policyName);
        var required = policyName[Prefix.Length..].Split('|', StringSplitOptions.RemoveEmptyEntries);
        return new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .RequireAssertion(ctx => required.Any(p => ctx.User.HasClaim(Permissions.ClaimType, p)))
            .Build();
    }
}
