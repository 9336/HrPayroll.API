using System.ComponentModel.DataAnnotations;

namespace HrPayroll.Contracts.Dtos;

/// <summary>A document an employee uploaded, with HR's verification.</summary>
public record EmployeeUploadDto(
    int UploadId,
    int EmployeeId,
    string EmployeeName,
    string EmployeeCode,
    string Category,
    string Title,
    string FileName,
    string ContentType,
    int SizeBytes,
    string Status,          // Pending / Verified / Rejected
    DateTime UploadedAt,
    string? ReviewedBy,
    DateTime? ReviewedAt,
    string? ReviewNote,
    bool CanDelete);

// The multipart upload form (EmployeeUploadForm) is in the Employees service - it carries an IFormFile.

public record UploadReviewRequest([MaxLength(500)] string? Note);

public record EmployeeUploadSummaryDto(int Pending, int Verified, int Rejected);
