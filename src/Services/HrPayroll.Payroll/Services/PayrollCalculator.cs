using HrPayroll.Payroll.Entities;

namespace HrPayroll.Payroll.Services;

/// <summary>
/// Pure payroll math, kept free of I/O so it is easy to unit test.
///   Per-day rate   = Gross / calendar days in month
///   LOP deduction  = Per-day rate x LOP days
///   PF             = Basic x (paid days / days in month) x PF%
///   PT & TDS       = fixed monthly amounts from the salary structure (0 if no paid days)
/// </summary>
public static class PayrollCalculator
{
    public static Payslip Calculate(SalaryStructure s, int year, int month, decimal lopDays)
    {
        var dim = DateTime.DaysInMonth(year, month);
        lopDays = Math.Clamp(lopDays, 0, dim);
        var paidDays = dim - lopDays;
        var gross = s.Gross;

        var lop = Round(gross / dim * lopDays);
        var pf = Round(s.BasicPay * paidDays / dim * s.PfPercent / 100m);
        var pt = paidDays > 0 ? s.ProfessionalTax : 0;
        var tds = paidDays > 0 ? s.MonthlyTds : 0;
        var deductions = lop + pf + pt + tds;

        return new Payslip
        {
            EmployeeId = s.EmployeeId,
            SalaryStructureId = s.SalaryStructureId,
            PayYear = year,
            PayMonth = (byte)month,
            DaysInMonth = (byte)dim,
            LopDays = lopDays,
            PaidDays = paidDays,
            BasicPay = s.BasicPay,
            Hra = s.Hra,
            ConveyanceAllowance = s.ConveyanceAllowance,
            MedicalAllowance = s.MedicalAllowance,
            SpecialAllowance = s.SpecialAllowance,
            GrossEarnings = gross,
            LopDeduction = lop,
            PfDeduction = pf,
            ProfessionalTax = pt,
            IncomeTax = tds,
            TotalDeductions = deductions,
            NetPay = Math.Max(0, gross - deductions),
            Status = PayslipStatus.Processed,
        };
    }

    /// <summary>Estimated take-home for a full month with no LOP.</summary>
    public static decimal EstimatedNet(SalaryStructure s) =>
        s.Gross - Round(s.BasicPay * s.PfPercent / 100m) - s.ProfessionalTax - s.MonthlyTds;

    private static decimal Round(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
}
