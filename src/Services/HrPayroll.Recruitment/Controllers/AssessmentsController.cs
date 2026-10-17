using HrPayroll.BuildingBlocks.Auth;
using HrPayroll.Contracts.Dtos;
using HrPayroll.Recruitment.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace HrPayroll.Recruitment.Controllers;

/// <summary>Question banks (team leads / managers) and candidate tests (recruiters).</summary>
[ApiController]
[Route("api/assessments")]
[Authorize]
public class AssessmentsController(AssessmentService assessments) : ControllerBase
{
    private AssessActor Actor => new(User.GetUserId(), User.GetEmployeeId(),
        Rights.Of(User, Permissions.AssessmentsModule), User.HasPermission(Permissions.RecruitmentView), User.HasPermission(Permissions.RecruitmentAdd));

    // ---------------------------------------------------------------- Banks
    /// <summary>Whether the caller can build question banks (team lead / manager / question bank rights).</summary>
    [HttpGet("access")]
    public async Task<ActionResult<object>> Access(CancellationToken ct) =>
        Ok(new { canManageBanks = await assessments.CanManageBanksAsync(Actor, ct), canAssignTests = Actor.CanAssignTests });

    [HttpGet("banks")]
    public async Task<ActionResult<List<QuestionBankDto>>> Banks(CancellationToken ct) => Ok(await assessments.GetBanksAsync(Actor, ct));

    [HttpGet("banks/{id:int}")]
    public async Task<ActionResult<QuestionBankDetailDto>> Bank(int id, CancellationToken ct) => Ok(await assessments.GetBankAsync(id, Actor, ct));

    [HttpPost("banks")]
    public async Task<ActionResult<QuestionBankDto>> CreateBank(QuestionBankRequest request, CancellationToken ct) =>
        Ok(await assessments.SaveBankAsync(null, request, Actor, ct));

    [HttpPut("banks/{id:int}")]
    public async Task<ActionResult<QuestionBankDto>> UpdateBank(int id, QuestionBankRequest request, CancellationToken ct) =>
        Ok(await assessments.SaveBankAsync(id, request, Actor, ct));

    [HttpPost("banks/{id:int}/questions")]
    public async Task<ActionResult<BankQuestionDto>> AddQuestion(int id, BankQuestionRequest request, CancellationToken ct) =>
        Ok(await assessments.SaveQuestionAsync(id, null, request, Actor, ct));

    [HttpPut("banks/{id:int}/questions/{questionId:int}")]
    public async Task<ActionResult<BankQuestionDto>> UpdateQuestion(int id, int questionId, BankQuestionRequest request, CancellationToken ct) =>
        Ok(await assessments.SaveQuestionAsync(id, questionId, request, Actor, ct));

    [HttpDelete("banks/{id:int}/questions/{questionId:int}")]
    public async Task<IActionResult> DeleteQuestion(int id, int questionId, CancellationToken ct)
    {
        await assessments.DeleteQuestionAsync(id, questionId, Actor, ct);
        return NoContent();
    }

    /// <summary>Bulk add (CSV import parsed by the client). All rows are validated before anything is saved.</summary>
    [HttpPost("banks/{id:int}/questions/import")]
    public async Task<ActionResult<object>> Import(int id, List<BankQuestionRequest> questions, CancellationToken ct) =>
        Ok(new { added = await assessments.ImportQuestionsAsync(id, questions, Actor, ct) });

    // ---------------------------------------------------------------- Candidate tests (recruiter)
    [HttpGet("tests")]
    [HasPermission(Permissions.RecruitmentView)]
    public async Task<ActionResult<List<CandidateTestDto>>> Tests([FromQuery] int? candidateId, [FromQuery] int? applicationId, CancellationToken ct) =>
        Ok(await assessments.GetTestsAsync(candidateId, applicationId, ct));

    [HttpGet("tests/{id:int}")]
    [HasPermission(Permissions.RecruitmentView)]
    public async Task<ActionResult<CandidateTestResultDto>> Result(int id, CancellationToken ct) => Ok(await assessments.GetResultAsync(id, ct));

    [HttpPost("tests")]
    [HasPermission(Permissions.RecruitmentAdd)]
    public async Task<ActionResult<CandidateTestDto>> Assign(AssignTestRequest request, CancellationToken ct) =>
        Ok(await assessments.AssignAsync(request, User.GetUserId(), ct));

    [HttpPost("tests/{id:int}/cancel")]
    [HasPermission(Permissions.RecruitmentEdit)]
    public async Task<ActionResult<CandidateTestDto>> Cancel(int id, CancellationToken ct) => Ok(await assessments.CancelTestAsync(id, User.GetUserId(), ct));
}

/// <summary>The candidate's test page - no login; the secret token in the link identifies the test.</summary>
[ApiController]
[Route("api/public/tests/{token}")]
[AllowAnonymous]
[EnableRateLimiting("public-test")]
public class PublicTestsController(AssessmentService assessments) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PublicTestDto>> Get(string token, CancellationToken ct) => Ok(await assessments.GetPublicAsync(token, ct));

    [HttpPost("start")]
    public async Task<ActionResult<PublicTestDto>> Start(string token, CancellationToken ct) => Ok(await assessments.StartAsync(token, ct));

    [HttpPut("answers")]
    public async Task<ActionResult<PublicTestDto>> Save(string token, SaveAnswersRequest request, CancellationToken ct) =>
        Ok(await assessments.SaveAnswersAsync(token, request, ct));

    [HttpPost("submit")]
    public async Task<ActionResult<PublicTestDto>> Submit(string token, SaveAnswersRequest request, CancellationToken ct) =>
        Ok(await assessments.SubmitAsync(token, request, ct));
}
