using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace HrPayroll.Contracts.Dtos;

public record DocumentSummaryDto(
    int DocumentId,
    int EmployeeId,
    string EmployeeCode,
    string EmployeeName,
    string DocumentType,
    string Title,
    string ReferenceNo,
    int? FinancialYear,
    DateOnly IssueDate,
    DateTime ReleasedOn,
    string? ReleasedBy);

/// <summary>A released document with its contents. <see cref="Data"/> shape depends on <see cref="DocumentType"/>.</summary>
public record DocumentDto(
    int DocumentId,
    int EmployeeId,
    string EmployeeCode,
    string EmployeeName,
    string DocumentType,
    string Title,
    string ReferenceNo,
    int? FinancialYear,
    DateOnly IssueDate,
    DateTime ReleasedOn,
    string? ReleasedBy,
    JsonElement Data);

// ---------------------------------------------------------------- Release requests
public abstract class ReleaseDocumentRequest
{
    [Range(1, int.MaxValue)] public int EmployeeId { get; set; }
    /// <summary>Date printed on the document (defaults to today).</summary>
    public DateOnly? IssueDate { get; set; }
}

public class OfferLetterRequest : ReleaseDocumentRequest
{
    [Required, MaxLength(100)] public string Designation { get; set; } = default!;
    [Required, MaxLength(100)] public string Department { get; set; } = default!;
    [Required] public DateOnly DateOfJoining { get; set; }
    /// <summary>Print the component-wise breakup from the employee's salary structure (CTC is then taken from it).</summary>
    public bool IncludeSalaryBreakup { get; set; } = true;
    /// <summary>Required when the breakup is not included.</summary>
    [Range(0, 1_000_000_000)] public decimal? AnnualCtc { get; set; }
    [Range(0, 24)] public int ProbationMonths { get; set; } = 6;
    [Range(0, 180)] public int NoticePeriodDays { get; set; } = 30;
    [MaxLength(150)] public string? WorkLocation { get; set; }
    /// <summary>Manager printed as "Reports to" (defaults to the employee's reporting manager).</summary>
    [Range(1, int.MaxValue)] public int? ReportingManagerId { get; set; }
    public DateOnly? AcceptBy { get; set; }
    [MaxLength(2000)] public string? AdditionalTerms { get; set; }
}

public class RelievingLetterRequest : ReleaseDocumentRequest
{
    public DateOnly? ResignationDate { get; set; }
    [Required] public DateOnly LastWorkingDay { get; set; }
    /// <summary>Designation at exit (defaults to the current designation).</summary>
    [MaxLength(100)] public string? Designation { get; set; }
    [MaxLength(1000)] public string? Remarks { get; set; }
}

public class Form16Request : ReleaseDocumentRequest
{
    /// <summary>First calendar year of the financial year: 2025 = FY 2025-26 (Apr 2025 - Mar 2026).</summary>
    [Range(2000, 2100)] public int FinancialYear { get; set; }
}

// ---------------------------------------------------------------- Document contents (stored as JSON)
public record SalaryLineDto(string Component, decimal Monthly, decimal Annual);

public record OfferLetterData(
    string EmployeeName,
    string EmployeeCode,
    string Email,
    string Designation,
    string Department,
    DateOnly DateOfJoining,
    decimal AnnualCtc,
    IReadOnlyList<SalaryLineDto>? Breakup,
    int ProbationMonths,
    int NoticePeriodDays,
    string? WorkLocation,
    DateOnly? AcceptBy,
    string? AdditionalTerms,
    string? ReportingTo = null);   // "Name, Designation" of the reporting manager

public record RelievingLetterData(
    string EmployeeName,
    string EmployeeCode,
    string Designation,
    string Department,
    DateOnly DateOfJoining,
    DateOnly? ResignationDate,
    DateOnly LastWorkingDay,
    string? Remarks,
    string? ReportingTo = null);   // manager at the time of exit

public record Form16MonthDto(int Year, int Month, decimal GrossEarned, decimal Pf, decimal ProfessionalTax, decimal Tds);

public record Form16QuarterDto(string Quarter, decimal AmountPaid, decimal TaxDeducted);

public record Form16Data(
    string EmployeeName,
    string EmployeeCode,
    string? EmployeePan,
    string Designation,
    string Department,
    string FinancialYear,
    string AssessmentYear,
    DateOnly PeriodFrom,
    DateOnly PeriodTo,
    bool IsProvisional,
    string TaxRegime,
    IReadOnlyList<Form16MonthDto> Months,
    IReadOnlyList<Form16QuarterDto> Quarters,
    decimal GrossSalary,
    decimal StandardDeduction,
    decimal IncomeChargeable,
    decimal ProfessionalTaxPaid,
    decimal PfContribution,
    decimal TotalTds);
