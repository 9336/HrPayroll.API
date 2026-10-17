using System.Net;

namespace HrPayroll.Attendance.Services;

/// <summary>
/// Reads an employee's ID-card photo from the Employees service (GET /internal/employees/{id}/photo) - registered faces
/// are compared with it. A synchronous call: photos are too big to copy through Kafka and are only needed here.
/// </summary>
public class IdPhotoClient(IHttpClientFactory http, ILogger<IdPhotoClient> logger)
{
    public const string Name = "employees";

    /// <summary>The photo, or null when the employee has none. Fails (503) when the Employees service cannot be reached.</summary>
    public async Task<byte[]?> GetAsync(int employeeId, CancellationToken ct)
    {
        try
        {
            using var response = await http.CreateClient(Name).GetAsync($"internal/employees/{employeeId}/photo", ct);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsByteArrayAsync(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("Could not read the ID photo of employee {EmployeeId}: {Error}", employeeId, ex.Message);
            throw new AppException("The face check is busy right now. Please try again in a minute.", StatusCodes.Status503ServiceUnavailable);
        }
    }

    /// <summary>Whether the employee has an ID photo (false when the Employees service cannot be reached).</summary>
    public async Task<bool> ExistsAsync(int employeeId, CancellationToken ct)
    {
        try { return await GetAsync(employeeId, ct) is not null; }
        catch (AppException) { return false; }
    }
}
