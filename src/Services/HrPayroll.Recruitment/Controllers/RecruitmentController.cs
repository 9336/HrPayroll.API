using HrPayroll.BuildingBlocks.Auth;
using HrPayroll.Contracts.Dtos;
using HrPayroll.Recruitment.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HrPayroll.Recruitment.Controllers;

/// <summary>Recruitment - recruitment.view / add / edit / delete, except the interviewer's own interviews and feedback.</summary>
[ApiController]
[Route("api/recruitment")]
[Authorize]
public class RecruitmentController(RecruitmentService recruitment) : ControllerBase
{
    private RecruiterActor ActorWith(string permission) => new(User.GetUserId(), User.GetEmployeeId(), User.HasPermission(permission));

    [HttpGet("dashboard")]
    [HasPermission(Permissions.RecruitmentView)]
    public async Task<ActionResult<RecruitmentDashboardDto>> Dashboard(CancellationToken ct) => Ok(await recruitment.GetDashboardAsync(ct));

    // ---------------------------------------------------------------- Jobs
    [HttpGet("jobs")]
    [HasPermission(Permissions.RecruitmentView)]
    public async Task<ActionResult<List<JobDto>>> Jobs([FromQuery] string? status, CancellationToken ct) => Ok(await recruitment.GetJobsAsync(status, ct));

    [HttpGet("jobs/{id:int}")]
    [HasPermission(Permissions.RecruitmentView)]
    public async Task<ActionResult<JobDto>> Job(int id, CancellationToken ct) => Ok(await recruitment.GetJobAsync(id, ct));

    [HttpPost("jobs")]
    [HasPermission(Permissions.RecruitmentAdd)]
    public async Task<ActionResult<JobDto>> CreateJob(JobRequest request, CancellationToken ct) =>
        Ok(await recruitment.SaveJobAsync(null, request, User.GetUserId(), ct));

    [HttpPut("jobs/{id:int}")]
    [HasPermission(Permissions.RecruitmentEdit)]
    public async Task<ActionResult<JobDto>> UpdateJob(int id, JobRequest request, CancellationToken ct) =>
        Ok(await recruitment.SaveJobAsync(id, request, User.GetUserId(), ct));

    [HttpGet("jobs/{id:int}/pipeline")]
    [HasPermission(Permissions.RecruitmentView)]
    public async Task<ActionResult<List<ApplicationDto>>> Pipeline(int id, CancellationToken ct) => Ok(await recruitment.GetPipelineAsync(id, ct));

    // ---------------------------------------------------------------- Candidates
    [HttpGet("candidates")]
    [HasPermission(Permissions.RecruitmentView)]
    public async Task<ActionResult<PagedResult<CandidateListItemDto>>> Candidates([FromQuery] string? search, [FromQuery] string? source,
        [FromQuery] int? jobId, [FromQuery] string? stage, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default) =>
        Ok(await recruitment.SearchCandidatesAsync(search, source, jobId, stage, page, pageSize, ct));

    [HttpGet("candidates/{id:int}")]
    [HasPermission(Permissions.RecruitmentView)]
    public async Task<ActionResult<CandidateDetailDto>> Candidate(int id, CancellationToken ct) => Ok(await recruitment.GetCandidateAsync(id, ct));

    [HttpPost("candidates")]
    [HasPermission(Permissions.RecruitmentAdd)]
    public async Task<ActionResult<CandidateDetailDto>> CreateCandidate(CandidateRequest request, CancellationToken ct) =>
        Ok(await recruitment.SaveCandidateAsync(null, request, User.GetUserId(), ct));

    [HttpPut("candidates/{id:int}")]
    [HasPermission(Permissions.RecruitmentEdit)]
    public async Task<ActionResult<CandidateDetailDto>> UpdateCandidate(int id, CandidateRequest request, CancellationToken ct) =>
        Ok(await recruitment.SaveCandidateAsync(id, request, User.GetUserId(), ct));

    /// <summary>Uploads the resume (multipart field "file": PDF or .docx, max 5 MB).</summary>
    [HttpPut("candidates/{id:int}/resume")]
    [HasPermission(Permissions.RecruitmentEdit)]
    [RequestSizeLimit(RecruitmentService.MaxResumeBytes + 64 * 1024)]
    public async Task<IActionResult> UploadResume(int id, IFormFile file, CancellationToken ct)
    {
        await recruitment.SetResumeAsync(id, file, User.GetUserId(), ct);
        return NoContent();
    }

    /// <summary>Recruiters, or employees who interview this candidate.</summary>
    [HttpGet("candidates/{id:int}/resume")]
    public async Task<IActionResult> Resume(int id, CancellationToken ct)
    {
        var (bytes, type, name) = await recruitment.GetResumeAsync(id, ActorWith(Permissions.RecruitmentView), ct);
        Response.Headers.XContentTypeOptions = "nosniff";
        return File(bytes, type, name);
    }

    // ---------------------------------------------------------------- Applications
    [HttpPost("applications")]
    [HasPermission(Permissions.RecruitmentAdd)]
    public async Task<ActionResult<ApplicationDto>> Apply(ApplyRequest request, CancellationToken ct) =>
        Ok(await recruitment.ApplyAsync(request, User.GetUserId(), ct));

    [HttpPut("applications/{id:int}/stage")]
    [HasPermission(Permissions.RecruitmentEdit)]
    public async Task<ActionResult<ApplicationDto>> Stage(int id, StageRequest request, CancellationToken ct) =>
        Ok(await recruitment.ChangeStageAsync(id, request, User.GetUserId(), ct));

    [HttpPut("applications/{id:int}/employee")]
    [HasPermission(Permissions.RecruitmentEdit, Permissions.EmployeesAdd)]
    public async Task<ActionResult<ApplicationDto>> LinkEmployee(int id, LinkEmployeeRequest request, CancellationToken ct) =>
        Ok(await recruitment.LinkEmployeeAsync(id, request.EmployeeId, User.GetUserId(), ct));

    // ---------------------------------------------------------------- Interviews
    [HttpGet("interviews")]
    [HasPermission(Permissions.RecruitmentView)]
    public async Task<ActionResult<List<InterviewDto>>> Interviews([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] string? status,
        [FromQuery] int? interviewerId, CancellationToken ct) =>
        Ok(await recruitment.GetInterviewsAsync(from, to, status, interviewerId, ct));

    /// <summary>Interviews the signed-in employee conducts.</summary>
    [HttpGet("interviews/my")]
    public async Task<ActionResult<List<InterviewDto>>> MyInterviews(CancellationToken ct) =>
        Ok(await recruitment.GetInterviewsAsync(null, null, null, User.RequireEmployeeId(), ct));

    [HttpPost("interviews")]
    [HasPermission(Permissions.RecruitmentAdd)]
    public async Task<ActionResult<InterviewDto>> Schedule(InterviewRequest request, CancellationToken ct) =>
        Ok(await recruitment.ScheduleAsync(request, User.GetUserId(), ct));

    [HttpPut("interviews/{id:int}")]
    [HasPermission(Permissions.RecruitmentEdit)]
    public async Task<ActionResult<InterviewDto>> Reschedule(int id, InterviewRequest request, CancellationToken ct) =>
        Ok(await recruitment.RescheduleAsync(id, request, User.GetUserId(), ct));

    [HttpPost("interviews/{id:int}/cancel")]
    [HasPermission(Permissions.RecruitmentEdit)]
    public async Task<ActionResult<InterviewDto>> Cancel(int id, CancelInterviewRequest request, CancellationToken ct) =>
        Ok(await recruitment.CancelInterviewAsync(id, request, User.GetUserId(), ct));

    /// <summary>The interviewer (or a recruiter) records the outcome.</summary>
    [HttpPost("interviews/{id:int}/feedback")]
    public async Task<ActionResult<InterviewDto>> Feedback(int id, FeedbackRequest request, CancellationToken ct) =>
        Ok(await recruitment.SubmitFeedbackAsync(id, request, ActorWith(Permissions.RecruitmentEdit), ct));

    // ---------------------------------------------------------------- Activity
    [HttpGet("activities")]
    [HasPermission(Permissions.RecruitmentView)]
    public async Task<ActionResult<List<ActivityDto>>> Activities([FromQuery] int? candidateId, [FromQuery] bool followUps = false,
        [FromQuery] int take = 50, CancellationToken ct = default) =>
        Ok(await recruitment.GetActivitiesAsync(candidateId, followUps, take, ct));

    [HttpPost("activities")]
    [HasPermission(Permissions.RecruitmentAdd)]
    public async Task<ActionResult<ActivityDto>> AddActivity(ActivityRequest request, CancellationToken ct) =>
        Ok(await recruitment.AddActivityAsync(request, User.GetUserId(), ct));

    [HttpPost("activities/{id:int}/done")]
    [HasPermission(Permissions.RecruitmentEdit)]
    public async Task<ActionResult<ActivityDto>> Done(int id, [FromQuery] bool done = true, CancellationToken ct = default) =>
        Ok(await recruitment.SetDoneAsync(id, done, ct));

    [HttpDelete("activities/{id:int}")]
    [HasPermission(Permissions.RecruitmentDelete)]
    public async Task<IActionResult> DeleteActivity(int id, CancellationToken ct)
    {
        await recruitment.DeleteActivityAsync(id, ct);
        return NoContent();
    }
}
