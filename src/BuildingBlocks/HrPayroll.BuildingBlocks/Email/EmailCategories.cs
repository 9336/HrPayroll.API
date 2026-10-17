namespace HrPayroll.BuildingBlocks.Email;

/// <summary>
/// Kinds of system email. Each can be switched off on the Email Settings page; switched-off emails are kept in the
/// log as "Skipped" instead of being sent.
/// </summary>
public static class EmailCategories
{
    public const string Leave = "Leave";
    public const string Documents = "Documents";
    public const string Payroll = "Payroll";
    public const string Promotion = "Promotion";
    public const string YearEnd = "YearEnd";
    public const string Visitors = "Visitors";
    public const string Task = "Task";
    public const string Appraisal = "Appraisal";
    public const string Recruitment = "Recruitment";
    public const string It = "IT";
    public const string Travel = "Travel";
    public const string Feedback = "Feedback";

    public record Definition(string Code, string Name, string Description);

    public static readonly IReadOnlyList<Definition> All =
    [
        new(Leave, "Leave", "Leave applied (to the reporting manager) and approved / rejected (to the employee)."),
        new(Documents, "Letters & Form 16", "Offer, relieving and promotion letters and Form 16 released to the employee."),
        new(Payroll, "Payslips", "\"Your payslip is ready\" when salary for a month is marked paid."),
        new(Promotion, "Promotions", "Promotion announcement to the employee and their reporting manager."),
        new(YearEnd, "Year-end", "New leave balances to every employee after the leave year is rolled over."),
        new(Visitors, "Visitors", "\"Your visitor has arrived\" to the host employee."),
        new(Task, "Tasks", "New or reassigned task to the assignee."),
        new(Appraisal, "Appraisals", "Appraisal form open, reminders, submitted and reviewed."),
        new(Feedback, "Appreciations & complaints", "Appreciation or complaint shared with the employee; their manager is told when someone else records one."),
        new(Recruitment, "Recruitment", "Interview invitations, online tests and results."),
        new(It, "IT requests", "Approval needed, approved, completed and rejected IT requests."),
        new(Travel, "Travel claims", "Approval needed, approved, rejected and paid travel claims."),
    ];

    public static readonly IReadOnlySet<string> Codes = All.Select(c => c.Code).ToHashSet();
}
