using System.Security.Cryptography;
using HrPayroll.BuildingBlocks.Auth;
using HrPayroll.BuildingBlocks.Common;
using HrPayroll.Recruitment.Data;
using HrPayroll.Contracts.Dtos;
using HrPayroll.BuildingBlocks.Email;
using HrPayroll.Recruitment.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using static HrPayroll.BuildingBlocks.Email.EmailTemplates;

namespace HrPayroll.Recruitment.Services;

/// <summary>
/// Who is acting: team leads / managers build and manage their own banks; <see cref="All"/> holds the
/// question-bank rights over every bank; recruiters (recruitment.view) see banks, recruitment.add assigns tests.
/// </summary>
public record AssessActor(int UserId, int? EmployeeId, Rights All, bool IsRecruiter, bool CanAssignTests);

/// <summary>
/// Candidate assessments. Team leads / managers build question banks ("branches") of multiple-choice
/// questions; a recruiter sends a candidate a test drawn at random from a bank (50 questions by default)
/// through a private link. The candidate takes the timed test without logging in; the server keeps the
/// clock, never sends the correct answers, and scores the test on submit or when time runs out.
/// </summary>
public class AssessmentService(RecruitmentDbContext db, OrgService org, EmailQueue mail, TimeProvider time)
{
    /// <summary>Answers arriving this long after the deadline are still accepted (network delay).</summary>
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(30);

    private DateTime Now => time.GetLocalNow().DateTime;

    // ================================================================== Banks
    public async Task<bool> CanManageBanksAsync(AssessActor a, CancellationToken ct) =>
        a.All.Add || a.All.Edit || a.All.Delete || await HasReportsAsync(a, ct);

    private async Task<bool> HasReportsAsync(AssessActor a, CancellationToken ct) =>
        a.EmployeeId is int me && await org.HasReportsAsync(me, ct);

    public async Task<List<QuestionBankDto>> GetBanksAsync(AssessActor actor, CancellationToken ct)
    {
        if (!actor.IsRecruiter && !actor.All.View && !await CanManageBanksAsync(actor, ct))
            throw new ForbiddenException("Question banks are for team leads, managers and recruiters.");
        return await ProjectBanks(db.QuestionBanks.AsNoTracking().OrderBy(b => b.Name), actor).ToListAsync(ct);
    }

    public async Task<QuestionBankDetailDto> GetBankAsync(int id, AssessActor actor, CancellationToken ct)
    {
        if (!actor.All.View) await EnsureBankEditorAsync(actor, ct);
        var bank = await ProjectBanks(db.QuestionBanks.AsNoTracking().Where(b => b.BankId == id), actor).FirstOrDefaultAsync(ct)
                   ?? throw new NotFoundException("Question bank not found.");
        var questions = await db.BankQuestions.AsNoTracking().Where(q => q.BankId == id)
            .OrderByDescending(q => q.IsActive).ThenBy(q => q.QuestionId)
            .Select(q => new BankQuestionDto(q.QuestionId, q.Text, q.OptionA, q.OptionB, q.OptionC, q.OptionD, q.CorrectOption, q.Difficulty,
                q.IsActive, db.CandidateTestQuestions.Count(t => t.QuestionId == q.QuestionId)))
            .ToListAsync(ct);
        return new QuestionBankDetailDto(bank, questions);
    }

    public async Task<QuestionBankDto> SaveBankAsync(int? id, QuestionBankRequest req, AssessActor actor, CancellationToken ct)
    {
        if (id is null && !actor.All.Add && !await HasReportsAsync(actor, ct))
            throw new ForbiddenException("You do not have permission to create question banks.");
        var name = req.Name.Trim();
        if (await db.QuestionBanks.AnyAsync(b => b.Name == name && b.BankId != id, ct)) throw new ConflictException($"A question bank named '{name}' already exists.");
        var bank = id is int existing ? await LoadEditableBankAsync(existing, actor, ct) : db.QuestionBanks.Add(new QuestionBank { CreatedByUserId = actor.UserId }).Entity;
        bank.Name = name;
        bank.Description = string.IsNullOrWhiteSpace(req.Description) ? null : req.Description.Trim();
        bank.IsActive = req.IsActive;
        await db.SaveChangesAsync(ct);
        return await ProjectBanks(db.QuestionBanks.AsNoTracking().Where(b => b.BankId == bank.BankId), actor).FirstAsync(ct);
    }

    public async Task<BankQuestionDto> SaveQuestionAsync(int bankId, int? questionId, BankQuestionRequest req, AssessActor actor, CancellationToken ct)
    {
        await LoadEditableBankAsync(bankId, actor, ct);
        Validate(req);
        BankQuestion q;
        if (questionId is int qid)
        {
            q = await db.BankQuestions.FirstOrDefaultAsync(x => x.QuestionId == qid && x.BankId == bankId, ct) ?? throw new NotFoundException("Question not found.");
            var used = await db.CandidateTestQuestions.AnyAsync(t => t.QuestionId == qid, ct);
            // A question already used in a test can only be switched on / off - its content must not change under a past result.
            if (used && (q.Text != req.Text.Trim() || q.OptionA != req.OptionA.Trim() || q.OptionB != req.OptionB.Trim() ||
                         q.OptionC != Clean(req.OptionC) || q.OptionD != Clean(req.OptionD) || q.CorrectOption != req.CorrectOption))
                throw new ConflictException("This question has already been used in a candidate test, so its text and answers cannot change. Deactivate it and add a new one.");
        }
        else
        {
            q = db.BankQuestions.Add(new BankQuestion { BankId = bankId, CreatedByUserId = actor.UserId }).Entity;
        }
        Apply(q, req);
        await db.SaveChangesAsync(ct);
        return new BankQuestionDto(q.QuestionId, q.Text, q.OptionA, q.OptionB, q.OptionC, q.OptionD, q.CorrectOption, q.Difficulty, q.IsActive,
            await db.CandidateTestQuestions.CountAsync(t => t.QuestionId == q.QuestionId, ct));
    }

    /// <summary>Adds many questions at once (CSV import). All-or-nothing.</summary>
    public async Task<int> ImportQuestionsAsync(int bankId, List<BankQuestionRequest> items, AssessActor actor, CancellationToken ct)
    {
        await LoadEditableBankAsync(bankId, actor, ct);
        if (items.Count == 0) throw new AppException("No questions to import.");
        if (items.Count > 500) throw new AppException("Import at most 500 questions at a time.");
        for (var i = 0; i < items.Count; i++)
        {
            try { Validate(items[i]); }
            catch (AppException ex) { throw new AppException($"Row {i + 1}: {ex.Message}"); }
        }
        foreach (var item in items)
        {
            var q = new BankQuestion { BankId = bankId, CreatedByUserId = actor.UserId };
            Apply(q, item);
            db.BankQuestions.Add(q);
        }
        await db.SaveChangesAsync(ct);
        return items.Count;
    }

    public async Task DeleteQuestionAsync(int bankId, int questionId, AssessActor actor, CancellationToken ct)
    {
        await LoadEditableBankAsync(bankId, actor, ct, delete: true);
        var q = await db.BankQuestions.FirstOrDefaultAsync(x => x.QuestionId == questionId && x.BankId == bankId, ct) ?? throw new NotFoundException("Question not found.");
        if (await db.CandidateTestQuestions.AnyAsync(t => t.QuestionId == questionId, ct))
            throw new ConflictException("This question has been used in a candidate test - deactivate it instead of deleting.");
        db.BankQuestions.Remove(q);
        await db.SaveChangesAsync(ct);
    }

    // ================================================================== Candidate tests (recruiter)
    public async Task<CandidateTestDto> AssignAsync(AssignTestRequest req, int userId, CancellationToken ct)
    {
        var app = await db.JobApplications.Include(a => a.Candidate).Include(a => a.Job).FirstOrDefaultAsync(a => a.ApplicationId == req.ApplicationId, ct)
                  ?? throw new NotFoundException("Application not found.");
        if (!ApplicationStage.Active.Contains(app.Stage)) throw new AppException($"The application is {app.Stage.ToLowerInvariant()} - a test cannot be sent.");
        var bank = await db.QuestionBanks.FirstOrDefaultAsync(b => b.BankId == req.BankId, ct) ?? throw new NotFoundException("Question bank not found.");
        if (!bank.IsActive) throw new AppException($"The question bank '{bank.Name}' is inactive.");
        var available = await db.BankQuestions.CountAsync(q => q.BankId == bank.BankId && q.IsActive, ct);
        if (available < req.QuestionCount)
            throw new AppException($"'{bank.Name}' has only {available} active question(s); the test needs {req.QuestionCount}. Add questions or lower the number.");
        if (await db.CandidateTests.AnyAsync(t => t.ApplicationId == app.ApplicationId && (t.Status == TestStatus.Assigned || t.Status == TestStatus.InProgress), ct))
            throw new ConflictException("This candidate already has an open test for this job. Cancel it first to send a new one.");

        // Random selection in SQL (NEWID()).
        var picked = await db.BankQuestions.Where(q => q.BankId == bank.BankId && q.IsActive)
            .OrderBy(q => Guid.NewGuid()).Take(req.QuestionCount).Select(q => q.QuestionId).ToListAsync(ct);

        var test = new CandidateTest
        {
            ApplicationId = app.ApplicationId, BankId = bank.BankId, Token = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32)),
            QuestionCount = picked.Count, DurationMinutes = req.DurationMinutes, PassPercent = req.PassPercent,
            ExpiresAt = Now.Date.AddDays(req.ExpiresInDays + 1).AddSeconds(-1),   // end of the last valid day
            Status = TestStatus.Assigned, CreatedByUserId = userId,
            Questions = picked.Select((id, i) => new CandidateTestQuestion { QuestionId = id, SortOrder = i + 1 }).ToList(),
        };
        db.CandidateTests.Add(test);
        LogActivity(app.CandidateId, app.ApplicationId, ActivityType.Interview,
            $"Online test sent: {bank.Name} ({picked.Count} questions, {req.DurationMinutes} min)", $"Link valid until {test.ExpiresAt:dd MMM yyyy}. Pass mark {req.PassPercent}%.", userId);

        if (req.NotifyCandidate)
        {
            var company = await CompanyNameAsync(ct);
            mail.Enqueue(app.Candidate.Email, app.Candidate.FullName, $"Online assessment for {app.Job.Title} - {company}",
                Layout(company, "Your online assessment",
                    P($"Dear {app.Candidate.FullName},") +
                    P($"As the next step for the {app.Job.Title} position, please complete a short online assessment: {picked.Count} multiple-choice questions in {req.DurationMinutes} minutes.") +
                    P($"Start it any time before {test.ExpiresAt:dd MMM yyyy}. Once you start, the timer cannot be paused, so make sure you have a quiet {req.DurationMinutes} minutes and a stable internet connection.") +
                    P("The link is personal - please do not share it."),
                    "Start the assessment", TestUrl(test.Token)),
                "Recruitment");
        }
        await db.SaveChangesAsync(ct);
        return await GetTestDtoAsync(test.TestId, ct);
    }

    public async Task<List<CandidateTestDto>> GetTestsAsync(int? candidateId, int? applicationId, CancellationToken ct)
    {
        await RefreshOpenTestsAsync(candidateId, applicationId, ct);
        var q = db.CandidateTests.AsNoTracking();
        if (candidateId is int c) q = q.Where(t => t.Application.CandidateId == c);
        if (applicationId is int a) q = q.Where(t => t.ApplicationId == a);
        return await ProjectTests(q.OrderByDescending(t => t.CreatedAt)).ToListAsync(ct);
    }

    public async Task<CandidateTestResultDto> GetResultAsync(int testId, CancellationToken ct)
    {
        var test = await db.CandidateTests.Include(t => t.Questions).FirstOrDefaultAsync(t => t.TestId == testId, ct) ?? throw new NotFoundException("Test not found.");
        if (await RefreshAsync(test, ct)) await db.SaveChangesAsync(ct);
        var questions = await db.CandidateTestQuestions.AsNoTracking().Where(q => q.TestId == testId).OrderBy(q => q.SortOrder)
            .Select(q => new TestResultQuestionDto(q.SortOrder, q.Question.Text, q.Question.OptionA, q.Question.OptionB, q.Question.OptionC,
                q.Question.OptionD, q.Question.CorrectOption, q.SelectedOption, q.IsCorrect, q.Question.Difficulty))
            .ToListAsync(ct);
        return new CandidateTestResultDto(await GetTestDtoAsync(testId, ct), questions);
    }

    public async Task<CandidateTestDto> CancelTestAsync(int testId, int userId, CancellationToken ct)
    {
        var test = await db.CandidateTests.Include(t => t.Application).FirstOrDefaultAsync(t => t.TestId == testId, ct) ?? throw new NotFoundException("Test not found.");
        if (test.Status is not (TestStatus.Assigned or TestStatus.InProgress)) throw new ConflictException($"The test is already {test.Status.ToLowerInvariant()}.");
        test.Status = TestStatus.Cancelled;
        LogActivity(test.Application.CandidateId, test.ApplicationId, ActivityType.Interview, "Online test cancelled", null, userId);
        await db.SaveChangesAsync(ct);
        return await GetTestDtoAsync(testId, ct);
    }

    // ================================================================== Public (candidate)
    public async Task<PublicTestDto> GetPublicAsync(string token, CancellationToken ct)
    {
        var test = await LoadByTokenAsync(token, ct);
        if (await RefreshAsync(test, ct)) await db.SaveChangesAsync(ct);
        return await ToPublicAsync(test, ct);
    }

    public async Task<PublicTestDto> StartAsync(string token, CancellationToken ct)
    {
        var test = await LoadByTokenAsync(token, ct);
        if (await RefreshAsync(test, ct)) { await db.SaveChangesAsync(ct); }
        if (test.Status == TestStatus.InProgress) return await ToPublicAsync(test, ct);   // reload / second tab: resume
        if (test.Status != TestStatus.Assigned) throw new ConflictException(StatusMessage(test.Status));

        test.Status = TestStatus.InProgress;
        test.StartedAt = Now;
        test.DeadlineAt = test.StartedAt.Value.AddMinutes(test.DurationMinutes);
        LogActivity(test.Application.CandidateId, test.ApplicationId, ActivityType.Interview, "Candidate started the online test", null, null);
        await db.SaveChangesAsync(ct);
        return await ToPublicAsync(test, ct);
    }

    public async Task<PublicTestDto> SaveAnswersAsync(string token, SaveAnswersRequest req, CancellationToken ct)
    {
        var test = await LoadByTokenAsync(token, ct);
        if (await RefreshAsync(test, ct)) { await db.SaveChangesAsync(ct); throw new ConflictException("Time is up - your answers were submitted automatically."); }
        if (test.Status != TestStatus.InProgress) throw new ConflictException(StatusMessage(test.Status));
        await ApplyAnswersAsync(test, req.Answers, ct);
        await db.SaveChangesAsync(ct);
        return await ToPublicAsync(test, ct);
    }

    public async Task<PublicTestDto> SubmitAsync(string token, SaveAnswersRequest req, CancellationToken ct)
    {
        var test = await LoadByTokenAsync(token, ct);
        if (await RefreshAsync(test, ct)) { await db.SaveChangesAsync(ct); return await ToPublicAsync(test, ct); }   // auto-submitted on time-out
        if (test.Status != TestStatus.InProgress) throw new ConflictException(StatusMessage(test.Status));
        await ApplyAnswersAsync(test, req.Answers, ct);
        await FinalizeAsync(test, timedOut: false, ct);
        await db.SaveChangesAsync(ct);
        return await ToPublicAsync(test, ct);
    }

    // ================================================================== Helpers
    private async Task ApplyAnswersAsync(CandidateTest test, List<PublicAnswer> answers, CancellationToken ct)
    {
        var options = await db.CandidateTestQuestions.Where(q => q.TestId == test.TestId)
            .Select(q => new { q.QuestionId, q.Question.OptionC, q.Question.OptionD }).ToDictionaryAsync(q => q.QuestionId, ct);
        foreach (var a in answers)
        {
            if (!options.TryGetValue(a.QuestionId, out var o)) throw new AppException("Unknown question.");
            if ((a.Option == "C" && o.OptionC is null) || (a.Option == "D" && o.OptionD is null)) throw new AppException("Invalid option.");
            test.Questions.First(q => q.QuestionId == a.QuestionId).SelectedOption = a.Option;
        }
    }

    /// <summary>Expires unopened tests and auto-submits tests whose time ran out. Returns true if anything changed.</summary>
    private async Task<bool> RefreshAsync(CandidateTest test, CancellationToken ct)
    {
        if (test.Status == TestStatus.Assigned && Now > test.ExpiresAt)
        {
            test.Status = TestStatus.Expired;
            await EnsureApplicationAsync(test, ct);
            LogActivity(test.Application.CandidateId, test.ApplicationId, ActivityType.Interview, "Online test expired without being started", null, null);
            return true;
        }
        if (test.Status == TestStatus.InProgress && Now > test.DeadlineAt!.Value + Grace)
        {
            await FinalizeAsync(test, timedOut: true, ct);
            return true;
        }
        return false;
    }

    private async Task RefreshOpenTestsAsync(int? candidateId, int? applicationId, CancellationToken ct)
    {
        var q = db.CandidateTests.Include(t => t.Questions).Where(t => t.Status == TestStatus.Assigned || t.Status == TestStatus.InProgress);
        if (candidateId is int c) q = q.Where(t => t.Application.CandidateId == c);
        if (applicationId is int a) q = q.Where(t => t.ApplicationId == a);
        var changed = false;
        foreach (var t in await q.ToListAsync(ct)) changed |= await RefreshAsync(t, ct);
        if (changed) await db.SaveChangesAsync(ct);
    }

    private async Task FinalizeAsync(CandidateTest test, bool timedOut, CancellationToken ct)
    {
        var ids = test.Questions.Select(x => x.QuestionId).ToList();
        var correct = await db.BankQuestions.Where(q => ids.Contains(q.QuestionId))
            .ToDictionaryAsync(q => q.QuestionId, q => q.CorrectOption, ct);
        foreach (var q in test.Questions) q.IsCorrect = q.SelectedOption is not null && q.SelectedOption == correct[q.QuestionId];

        test.Status = TestStatus.Submitted;
        test.SubmittedAt = timedOut ? test.DeadlineAt : Now;
        test.CorrectAnswers = test.Questions.Count(q => q.IsCorrect == true);
        test.ScorePercent = test.QuestionCount == 0 ? 0 : Math.Round(test.CorrectAnswers.Value * 100m / test.QuestionCount, 2);
        test.Passed = test.ScorePercent >= test.PassPercent;

        await EnsureApplicationAsync(test, ct);
        var app = test.Application;
        var summary = $"Online test {(timedOut ? "auto-submitted (time up)" : "submitted")}: {test.CorrectAnswers}/{test.QuestionCount} ({test.ScorePercent:0.#}%) - {(test.Passed == true ? "PASSED" : "not passed")}";
        LogActivity(app.CandidateId, app.ApplicationId, ActivityType.Feedback, summary,
            $"{test.Questions.Count(q => q.SelectedOption is not null)} of {test.QuestionCount} answered. Pass mark {test.PassPercent}%.", null);

        // Tell whoever sent the test.
        var sender = await db.Users.Where(u => u.UserId == test.CreatedByUserId && u.Employee != null)
            .Select(u => new { u.Employee!.Email, Name = u.Employee.FirstName + " " + u.Employee.LastName }).FirstOrDefaultAsync(ct);
        if (sender is not null)
        {
            mail.Enqueue(sender.Email, sender.Name, $"Test result: {app.Candidate.FullName} - {test.ScorePercent:0.#}%",
                Layout(await CompanyNameAsync(ct), "Candidate test completed",
                    P($"{app.Candidate.FullName} ({app.Job.Title}) completed the online test.") + P(summary),
                    "Open candidate", $"{mail.AppUrl}/admin/recruitment/candidates/{app.CandidateId}"),
                "Recruitment");
        }
    }

    private async Task EnsureApplicationAsync(CandidateTest test, CancellationToken ct)
    {
        if (test.Application is null || test.Application.Candidate is null || test.Application.Job is null)
            test.Application = await db.JobApplications.Include(a => a.Candidate).Include(a => a.Job).FirstAsync(a => a.ApplicationId == test.ApplicationId, ct);
    }

    private async Task<CandidateTest> LoadByTokenAsync(string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length != 43) throw new NotFoundException("This test link is not valid.");
        return await db.CandidateTests.Include(t => t.Questions).Include(t => t.Bank)
                   .Include(t => t.Application).ThenInclude(a => a.Candidate)
                   .Include(t => t.Application).ThenInclude(a => a.Job)
                   .FirstOrDefaultAsync(t => t.Token == token, ct)
               ?? throw new NotFoundException("This test link is not valid.");
    }

    private async Task<PublicTestDto> ToPublicAsync(CandidateTest t, CancellationToken ct)
    {
        IReadOnlyList<PublicQuestionDto>? questions = null;
        int? remaining = null;
        if (t.Status == TestStatus.InProgress)
        {
            remaining = Math.Max(0, (int)(t.DeadlineAt!.Value - Now).TotalSeconds);
            var selected = t.Questions.ToDictionary(q => q.QuestionId, q => q.SelectedOption);
            questions = (await db.CandidateTestQuestions.AsNoTracking().Where(q => q.TestId == t.TestId).OrderBy(q => q.SortOrder)
                    .Select(q => new { q.QuestionId, q.SortOrder, q.Question.Text, q.Question.OptionA, q.Question.OptionB, q.Question.OptionC, q.Question.OptionD })
                    .ToListAsync(ct))
                .Select(q => new PublicQuestionDto(q.QuestionId, q.SortOrder, q.Text,
                    new[] { ("A", q.OptionA), ("B", q.OptionB), ("C", q.OptionC), ("D", q.OptionD) }
                        .Where(o => o.Item2 is not null).Select(o => new PublicOptionDto(o.Item1, o.Item2!)).ToList(),
                    selected.GetValueOrDefault(q.QuestionId)))
                .ToList();
        }
        return new PublicTestDto(t.Application.Candidate.FullName, t.Application.Job.Title, await CompanyNameAsync(ct), t.Bank.Name,
            t.QuestionCount, t.DurationMinutes, t.ExpiresAt, t.Status, remaining, questions);
    }

    private async Task EnsureBankEditorAsync(AssessActor actor, CancellationToken ct)
    {
        if (!await CanManageBanksAsync(actor, ct)) throw new ForbiddenException("Only team leads, managers or users with question bank permissions can do this.");
    }

    /// <summary>A bank the actor created, or any bank with question banks: Edit (Delete for deleting questions).</summary>
    private async Task<QuestionBank> LoadEditableBankAsync(int id, AssessActor actor, CancellationToken ct, bool delete = false)
    {
        await EnsureBankEditorAsync(actor, ct);
        var bank = await db.QuestionBanks.FirstOrDefaultAsync(b => b.BankId == id, ct) ?? throw new NotFoundException("Question bank not found.");
        if (!(delete ? actor.All.Delete : actor.All.Edit) && bank.CreatedByUserId != actor.UserId)
            throw new ForbiddenException(delete ? "You can only delete questions from banks you created." : "You can only edit question banks you created.");
        return bank;
    }

    private static void Validate(BankQuestionRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Text) || string.IsNullOrWhiteSpace(r.OptionA) || string.IsNullOrWhiteSpace(r.OptionB))
            throw new AppException("The question and options A and B are required.");
        if (string.IsNullOrWhiteSpace(r.OptionC) && !string.IsNullOrWhiteSpace(r.OptionD)) throw new AppException("Fill option C before option D.");
        if ((r.CorrectOption == "C" && string.IsNullOrWhiteSpace(r.OptionC)) || (r.CorrectOption == "D" && string.IsNullOrWhiteSpace(r.OptionD)))
            throw new AppException($"The correct option {r.CorrectOption} is empty.");
        if (r.CorrectOption is not ("A" or "B" or "C" or "D")) throw new AppException("Correct option must be A, B, C or D.");
        if (r.Difficulty is not ("Easy" or "Medium" or "Hard")) throw new AppException("Difficulty must be Easy, Medium or Hard.");
        var opts = new[] { r.OptionA, r.OptionB, r.OptionC, r.OptionD }.Where(o => !string.IsNullOrWhiteSpace(o)).Select(o => o!.Trim().ToLowerInvariant()).ToList();
        if (opts.Distinct().Count() != opts.Count) throw new AppException("Options must be different from each other.");
    }

    private static void Apply(BankQuestion q, BankQuestionRequest r)
    {
        q.Text = r.Text.Trim();
        q.OptionA = r.OptionA.Trim();
        q.OptionB = r.OptionB.Trim();
        q.OptionC = Clean(r.OptionC);
        q.OptionD = Clean(r.OptionD);
        q.CorrectOption = r.CorrectOption;
        q.Difficulty = r.Difficulty;
        q.IsActive = r.IsActive;
    }

    private void LogActivity(int candidateId, int applicationId, string type, string summary, string? details, int? userId) =>
        db.RecruitmentActivities.Add(new RecruitmentActivity
        {
            CandidateId = candidateId, ApplicationId = applicationId, Type = type, Summary = summary.Length > 200 ? summary[..200] : summary,
            Details = details, CreatedByUserId = userId, CreatedAt = time.GetUtcNow().UtcDateTime,
        });

    private string TestUrl(string token) => $"{mail.AppUrl}/test/{token}";

    private async Task<CandidateTestDto> GetTestDtoAsync(int id, CancellationToken ct) =>
        await ProjectTests(db.CandidateTests.AsNoTracking().Where(t => t.TestId == id)).FirstAsync(ct);

    private IQueryable<CandidateTestDto> ProjectTests(IQueryable<CandidateTest> q)
    {
        var baseUrl = $"{mail.AppUrl}/test/";
        return q.Select(t => new CandidateTestDto(t.TestId, t.ApplicationId, t.Application.Job.Title, t.Application.CandidateId, t.Application.Candidate.FullName,
            t.BankId, t.Bank.Name, t.QuestionCount, t.DurationMinutes, t.PassPercent, t.ExpiresAt, t.Status, t.StartedAt, t.SubmittedAt,
            t.CorrectAnswers, t.ScorePercent, t.Passed, t.Questions.Count(x => x.SelectedOption != null), baseUrl + t.Token, t.CreatedAt));
    }

    private IQueryable<QuestionBankDto> ProjectBanks(IQueryable<QuestionBank> q, AssessActor actor) =>
        q.Select(b => new QuestionBankDto(b.BankId, b.Name, b.Description, b.IsActive,
            db.BankQuestions.Count(x => x.BankId == b.BankId && x.IsActive), db.BankQuestions.Count(x => x.BankId == b.BankId),
            b.CreatedBy.Employee != null ? b.CreatedBy.Employee.FirstName + " " + b.CreatedBy.Employee.LastName : b.CreatedBy.Username,
            b.CreatedAt, actor.All.Edit || b.CreatedByUserId == actor.UserId, actor.All.Delete || b.CreatedByUserId == actor.UserId));

    private async Task<string> CompanyNameAsync(CancellationToken ct) =>
        await db.CompanyProfiles.Select(c => c.LegalName ?? c.Name).FirstOrDefaultAsync(ct) ?? "HR Payroll";

    private static string StatusMessage(string status) => status switch
    {
        TestStatus.Submitted => "This test has already been submitted. Thank you!",
        TestStatus.Expired => "This test link has expired. Please contact the recruiter.",
        TestStatus.Cancelled => "This test has been cancelled. Please contact the recruiter.",
        _ => "This test cannot be taken right now.",
    };

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
