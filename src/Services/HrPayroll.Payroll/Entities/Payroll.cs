namespace HrPayroll.Payroll.Entities;

public static class PayslipStatus
{
    public const string Processed = "Processed";
    public const string Paid = "Paid";
}

public class SalaryStructure
{
    public int SalaryStructureId { get; set; }
    public int EmployeeId { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    public decimal BasicPay { get; set; }
    public decimal Hra { get; set; }
    public decimal ConveyanceAllowance { get; set; }
    public decimal MedicalAllowance { get; set; }
    public decimal SpecialAllowance { get; set; }
    public decimal PfPercent { get; set; }
    public decimal ProfessionalTax { get; set; }
    public decimal MonthlyTds { get; set; }
    public decimal GrossMonthly { get; private set; }   // computed column in SQL
    public string? Remarks { get; set; }
    public int? CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }

    public Employee Employee { get; set; } = default!;

    public decimal Gross => BasicPay + Hra + ConveyanceAllowance + MedicalAllowance + SpecialAllowance;
}

public class Payslip
{
    public int PayslipId { get; set; }
    public int EmployeeId { get; set; }
    public int SalaryStructureId { get; set; }
    public int PayYear { get; set; }
    public byte PayMonth { get; set; }
    public byte DaysInMonth { get; set; }
    public decimal LopDays { get; set; }
    public decimal PaidDays { get; set; }
    public decimal BasicPay { get; set; }
    public decimal Hra { get; set; }
    public decimal ConveyanceAllowance { get; set; }
    public decimal MedicalAllowance { get; set; }
    public decimal SpecialAllowance { get; set; }
    public decimal GrossEarnings { get; set; }
    public decimal LopDeduction { get; set; }
    public decimal PfDeduction { get; set; }
    public decimal ProfessionalTax { get; set; }
    public decimal IncomeTax { get; set; }
    public decimal TotalDeductions { get; set; }
    public decimal NetPay { get; set; }
    public string Status { get; set; } = PayslipStatus.Processed;
    public DateTime GeneratedOn { get; set; }
    public int? GeneratedByUserId { get; set; }
    public DateTime? PaidOn { get; set; }

    public Employee Employee { get; set; } = default!;
    public SalaryStructure SalaryStructure { get; set; } = default!;
}
