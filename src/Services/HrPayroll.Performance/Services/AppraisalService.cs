using HrPayroll.BuildingBlocks.Auth;
using HrPayroll.BuildingBlocks.Common;
using HrPayroll.Performance.Data;
using HrPayroll.Contracts.Dtos;
using HrPayroll.BuildingBlocks.Email;
using HrPayroll.Performance.Entities;
using Microsoft.EntityFrameworkCore;
using static HrPayroll.BuildingBlocks.Email.EmailTemplates;

namespace HrPayroll.Performance.Services;

/// <summary>Who is acting: their login, their employee record and whether they manage appraisals.</summary>
/// <summary>CanManage: see every appraisal (appraisals.view); CanReviewAll: review anyone's (appraisals.edit).</summary>
public record AppraisalActor(int UserId, int? EmployeeId, bool CanManage, bool CanReviewAll = false);

/// <summary>
/// Appraisals: HR / admin create a cycle with a form and the window in which employees can fill it;
/// employees self-assess and submit; the reporting manager (or HR / admin) reviews. Every step emails the
/// people involved through the outbox.
/// </summary>
public class AppraisalService(PerformanceDbContext db, EmailQueue mail, FeedbackService feedback, TimeProvider time)
{
    private const string Category = "Appraisal";

    private DateOnly Today => DateOnly.FromDateTime(time.GetLocalNow().DateTime);

    public static string StateOf(AppraisalCycle c, DateOnly today) =>
        !c.IsPublished ? "Draft" : today < c.OpensOn ? "Scheduled" : today <= c.ClosesOn ? "Open" : "Closed";

    // ================================================================== Cycles (HR / admin)
    public async Task<List<AppraisalCycleDto>> GetCyclesAsync(CancellationToken ct)
    {
        var cycles = await db.AppraisalCycles.AsNoTracking().Include(c => c.Questions)
            .OrderByDescending(c => c.OpensOn).ThenByDescending(c => c.CycleId).ToListAsync(ct);
        var result = new List<AppraisalCycleDto>();
        foreach (var c in cycles) result.Add(ToDto(c, await CountsAsync(c, ct)));
        return result;
    }

    public async Task<AppraisalCycleDto> GetCycleAsync(int id, CancellationToken ct)
    {
        var c = await db.AppraisalCycles.AsNoTracking().Include(x => x.Questions).FirstOrDefaultAsync(x => x.CycleId == id, ct)
                ?? throw new NotFoundException("Appraisal cycle not found.");
        return ToDto(c, await CountsAsync(c, ct));
    }

    public async Task<AppraisalCycleDto> CreateCycleAsync(CycleRequest req, int userId, CancellationToken ct)
    {
        ValidateDates(req);
        var cycle = new AppraisalCycle { CreatedByUserId = userId };
        Apply(cycle, req);

        if (req.CopyQuestionsFromCycleId is int sourceId)
        {
            var source = await db.AppraisalQuestions.AsNoTracking().Where(q => q.CycleId == sourceId).OrderBy(q => q.SortOrder).ToListAsync(ct);
            if (source.Count == 0) throw new AppException("The selected cycle has no questions to copy.");
            cycle.Questions = source.Select(q => new AppraisalQuestion
                { SortOrder = q.SortOrder, Section = q.Section, Text = q.Text, Kind = q.Kind, IsRequired = q.IsRequired }).ToList();
        }
        else
        {
            cycle.Questions = DefaultQuestions();
        }

        db.AppraisalCycles.Add(cycle);
        await db.SaveChangesAsync(ct);
        return await GetCycleAsync(cycle.CycleId, ct);
    }

    /// <summary>Dates can change after publishing (e.g. to extend the window); the form cannot.</summary>
    public async Task<AppraisalCycleDto> UpdateCycleAsync(int id, CycleRequest req, CancellationToken ct)
    {
        ValidateDates(req);
        var cycle = await LoadCycleAsync(id, ct);
        if (cycle.IsPublished && req.OpensOn != cycle.OpensOn && cycle.OpensOn <= Today)
            throw new AppException("The form is already open - its opening date cannot be changed (you can still change the closing date).");
        Apply(cycle, req);
        await db.SaveChangesAsync(ct);
        return await GetCycleAsync(id, ct);
    }

    public async Task<AppraisalCycleDto> SaveQuestionsAsync(int id, List<QuestionRequest> questions, CancellationToken ct)
    {
        var cycle = await db.AppraisalCycles.Include(c => c.Questions).FirstOrDefaultAsync(c => c.CycleId == id, ct)
                    ?? throw new NotFoundException("Appraisal cycle not found.");
        if (cycle.IsPublished) throw new ConflictException("The form cannot be changed after the cycle is published.");
        if (questions.Count == 0) throw new AppException("The form needs at least one question.");
        if (questions.Count > 50) throw new AppException("A form can have at most 50 questions.");

        db.AppraisalQuestions.RemoveRange(cycle.Questions);
        cycle.Questions = questions.Select((q, i) => new AppraisalQuestion
        {
            SortOrder = i + 1,
            Section = string.IsNullOrWhiteSpace(q.Section) ? null : q.Section.Trim(),
            Text = q.Text.Trim(),
            Kind = q.Kind,
            IsRequired = q.IsRequired,
        }).ToList();
        await db.SaveChangesAsync(ct);
        return await GetCycleAsync(id, ct);
    }

    /// <summary>Publishes the cycle and emails every eligible employee (delivered on the opening day).</summary>
    public async Task<AppraisalCycleDto> PublishAsync(int id, CancellationToken ct)
    {
        var cycle = await db.AppraisalCycles.Include(c => c.Questions).FirstOrDefaultAsync(c => c.CycleId == id, ct)
                    ?? throw new NotFoundException("Appraisal cycle not found.");
        if (cycle.IsPublished) throw new ConflictException("This cycle is already published.");
        if (cycle.Questions.Count == 0) throw new AppException("Add at least one question before publishing.");
        if (cycle.ClosesOn < Today) throw new AppException("The closing date has already passed. Change the dates first.");

        cycle.IsPublished = true;
        cycle.PublishedAt = time.GetUtcNow().UtcDateTime;

        var company = await CompanyNameAsync(ct);
        var deliverAt = cycle.OpensOn.ToDateTime(new TimeOnly(9, 0));   // 9 AM on the opening day (or now if already open)
        foreach (var e in await EligibleQuery(cycle).Select(e => new { e.Email, e.FirstName, e.LastName }).ToListAsync(ct))
        {
            mail.Enqueue(e.Email, $"{e.FirstName} {e.LastName}", $"{cycle.Name}: your appraisal form is open",
                Layout(company, "Your appraisal form is open",
                    P($"Dear {e.FirstName},") +
                    P($"The {cycle.Name} (review period {cycle.PeriodFrom:dd MMM yyyy} - {cycle.PeriodTo:dd MMM yyyy}) is now open.") +
                    P($"Please complete and submit your self-appraisal between {cycle.OpensOn:dd MMM yyyy} and {cycle.ClosesOn:dd MMM yyyy}."),
                    "Open my appraisal", $"{mail.AppUrl}/me/appraisal/{cycle.CycleId}"),
                Category, deliverAt);
        }
        await db.SaveChangesAsync(ct);
        return await GetCycleAsync(id, ct);
    }

    public async Task DeleteCycleAsync(int id, CancellationToken ct)
    {
        var cycle = await LoadCycleAsync(id, ct);
        if (cycle.IsPublished) throw new ConflictException("A published cycle cannot be deleted.");
        db.AppraisalCycles.Remove(cycle);
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<ProgressRowDto>> GetProgressAsync(int id, CancellationToken ct)
    {
        var cycle = await LoadCycleAsync(id, ct);
        var appraisals = await db.Appraisals.AsNoTracking().Where(a => a.CycleId == id)
            .Select(a => new
            {
                a.EmployeeId, a.AppraisalId, a.Status, a.SubmittedAt, a.FinalRating,
                Reviewer = a.Reviewer != null ? a.Reviewer.FirstName + " " + a.Reviewer.LastName : null,
            })
            .ToDictionaryAsync(a => a.EmployeeId, ct);
        var employees = await EligibleQuery(cycle)
            .Select(e => new
            {
                e.EmployeeId, e.EmployeeCode, Name = e.FirstName + " " + e.LastName, Dept = e.Department.Name,
                Manager = e.Manager != null ? e.Manager.FirstName + " " + e.Manager.LastName : null,
            })
            .OrderBy(e => e.EmployeeCode).ToListAsync(ct);

        var counts = await feedback.CountsAsync(cycle.PeriodFrom, cycle.PeriodTo, ct);
        return employees.Select(e =>
        {
            var a = appraisals.GetValueOrDefault(e.EmployeeId);
            var (appreciations, complaints) = counts.GetValueOrDefault(e.EmployeeId);
            return new ProgressRowDto(e.EmployeeId, e.EmployeeCode, e.Name, e.Dept, a?.Status ?? AppraisalStatus.NotStarted,
                a?.AppraisalId, a?.Reviewer ?? e.Manager, a?.SubmittedAt, a?.FinalRating, appreciations, complaints);
        }).ToList();
    }

    /// <summary>Emails everyone who has not submitted yet. Returns how many were reminded.</summary>
    public async Task<int> RemindAsync(int id, CancellationToken ct)
    {
        var cycle = await LoadCycleAsync(id, ct);
        if (StateOf(cycle, Today) != "Open") throw new AppException("Reminders can only be sent while the form is open.");

        var submitted = db.Appraisals.Where(a => a.CycleId == id && a.Status != AppraisalStatus.Draft).Select(a => a.EmployeeId);
        var pending = await EligibleQuery(cycle).Where(e => !submitted.Contains(e.EmployeeId))
            .Select(e => new { e.Email, e.FirstName, e.LastName }).ToListAsync(ct);
        var company = await CompanyNameAsync(ct);
        var daysLeft = cycle.ClosesOn.DayNumber - Today.DayNumber;
        foreach (var e in pending)
        {
            mail.Enqueue(e.Email, $"{e.FirstName} {e.LastName}", $"Reminder: submit your {cycle.Name} self-appraisal",
                Layout(company, "Appraisal reminder",
                    P($"Dear {e.FirstName},") +
                    P($"Your self-appraisal for {cycle.Name} has not been submitted yet. The form closes on {cycle.ClosesOn:dd MMM yyyy}" +
                      (daysLeft == 0 ? " (today)." : $" ({daysLeft} day(s) left).")),
                    "Complete my appraisal", $"{mail.AppUrl}/me/appraisal/{cycle.CycleId}"),
                Category);
        }
        await db.SaveChangesAsync(ct);
        return pending.Count;
    }

    /// <summary>Sends a submitted appraisal back to the employee as a draft (e.g. on request).</summary>
    public async Task ReopenAsync(int appraisalId, CancellationToken ct)
    {
        var a = await db.Appraisals.Include(x => x.Employee).Include(x => x.Cycle)
                    .FirstOrDefaultAsync(x => x.AppraisalId == appraisalId, ct)
                ?? throw new NotFoundException("Appraisal not found.");
        if (a.Status != AppraisalStatus.Submitted) throw new ConflictException("Only submitted (not yet reviewed) appraisals can be reopened.");
        if (Today > a.Cycle.ClosesOn) throw new AppException("The form has closed. Extend the closing date first so the employee can resubmit.");
        a.Status = AppraisalStatus.Draft;
        a.SubmittedAt = null;
        a.UpdatedAt = time.GetUtcNow().UtcDateTime;
        mail.Enqueue(a.Employee.Email, a.Employee.FullName, $"{a.Cycle.Name}: your appraisal was reopened",
            Layout(await CompanyNameAsync(ct), "Your appraisal was reopened",
                P($"Dear {a.Employee.FirstName},") +
                P($"Your self-appraisal for {a.Cycle.Name} has been reopened so you can update it. Please submit it again by {a.Cycle.ClosesOn:dd MMM yyyy}."),
                "Open my appraisal", $"{mail.AppUrl}/me/appraisal/{a.CycleId}"),
            Category);
        await db.SaveChangesAsync(ct);
    }

    // ================================================================== Employee (self-appraisal)
    public async Task<List<MyCycleDto>> GetMyCyclesAsync(int employeeId, CancellationToken ct)
    {
        var joined = await db.Employees.Where(e => e.EmployeeId == employeeId).Select(e => e.DateOfJoining).FirstAsync(ct);
        var cycles = await db.AppraisalCycles.AsNoTracking()
            .Where(c => c.IsPublished && c.PeriodTo >= joined)
            .OrderByDescending(c => c.OpensOn).ToListAsync(ct);
        var mine = await db.Appraisals.AsNoTracking().Where(a => a.EmployeeId == employeeId)
            .ToDictionaryAsync(a => a.CycleId, a => new { a.Status, a.FinalRating }, ct);
        var today = Today;
        return cycles.Select(c =>
        {
            var a = mine.GetValueOrDefault(c.CycleId);
            return new MyCycleDto(c.CycleId, c.Name, c.PeriodFrom, c.PeriodTo, c.OpensOn, c.ClosesOn, StateOf(c, today),
                a?.Status ?? AppraisalStatus.NotStarted, a?.Status == AppraisalStatus.Reviewed ? a.FinalRating : null);
        }).ToList();
    }

    public async Task<AppraisalFormDto> GetMyFormAsync(int employeeId, int cycleId, CancellationToken ct)
    {
        var cycle = await db.AppraisalCycles.AsNoTracking().Include(c => c.Questions).FirstOrDefaultAsync(c => c.CycleId == cycleId && c.IsPublished, ct)
                    ?? throw new NotFoundException("Appraisal not found.");
        await EnsureEligibleAsync(cycle, employeeId, ct);
        var appraisal = await LoadAppraisalAsync(a => a.CycleId == cycleId && a.EmployeeId == employeeId, tracking: false, ct);
        return await ToFormAsync(cycle, employeeId, appraisal, asReviewer: false, canReview: false, ct);
    }

    public async Task<AppraisalFormDto> SaveMyAsync(int employeeId, int cycleId, SelfAppraisalRequest req, CancellationToken ct)
    {
        var cycle = await db.AppraisalCycles.AsNoTracking().Include(c => c.Questions).FirstOrDefaultAsync(c => c.CycleId == cycleId && c.IsPublished, ct)
                    ?? throw new NotFoundException("Appraisal not found.");
        await EnsureEligibleAsync(cycle, employeeId, ct);
        var state = StateOf(cycle, Today);
        if (state == "Scheduled") throw new AppException($"The appraisal form opens on {cycle.OpensOn:dd MMM yyyy}.");
        if (state == "Closed") throw new AppException($"The appraisal form closed on {cycle.ClosesOn:dd MMM yyyy}.");

        var appraisal = await LoadAppraisalAsync(a => a.CycleId == cycleId && a.EmployeeId == employeeId, tracking: true, ct);
        if (appraisal is null)
        {
            appraisal = new Appraisal { CycleId = cycleId, EmployeeId = employeeId, Status = AppraisalStatus.Draft };
            db.Appraisals.Add(appraisal);
        }
        if (appraisal.Status != AppraisalStatus.Draft) throw new ConflictException("Your appraisal has already been submitted.");

        var questions = cycle.Questions.ToDictionary(q => q.QuestionId);
        foreach (var input in req.Answers)
        {
            if (!questions.TryGetValue(input.QuestionId, out var q)) throw new AppException("Unknown question in the form.");
            var answer = appraisal.Answers.FirstOrDefault(x => x.QuestionId == q.QuestionId);
            if (answer is null) appraisal.Answers.Add(answer = new AppraisalAnswer { QuestionId = q.QuestionId });
            answer.SelfRating = q.Kind == QuestionKind.Rating ? (byte?)input.Rating : null;
            answer.SelfComment = Clean(input.Comment);
        }
        appraisal.SelfSummary = Clean(req.SelfSummary);
        appraisal.UpdatedAt = time.GetUtcNow().UtcDateTime;

        if (req.Submit)
        {
            var missing = cycle.Questions.Where(q => q.IsRequired && !Answered(q, appraisal.Answers.FirstOrDefault(a => a.QuestionId == q.QuestionId), self: true))
                .Select(q => q.SortOrder).ToList();
            if (missing.Count > 0) throw new AppException($"Please answer all required questions before submitting (question {string.Join(", ", missing)}).");

            var emp = await db.Employees.AsNoTracking().Where(e => e.EmployeeId == employeeId)
                .Select(e => new { e.FirstName, e.LastName, e.Email, e.ManagerId }).FirstAsync(ct);
            appraisal.Status = AppraisalStatus.Submitted;
            appraisal.SubmittedAt = time.GetUtcNow().UtcDateTime;
            appraisal.ReviewerEmployeeId = emp.ManagerId;
            await QueueSubmittedEmailsAsync(cycle, appraisal, $"{emp.FirstName} {emp.LastName}", emp.FirstName, emp.Email, emp.ManagerId, ct);
        }

        await db.SaveChangesAsync(ct);
        return await GetMyFormAsync(employeeId, cycleId, ct);
    }

    // ================================================================== Review (manager / HR / admin)
    public async Task<List<ReviewListItemDto>> GetReviewListAsync(AppraisalActor actor, int? cycleId, string? status, CancellationToken ct)
    {
        var query = db.Appraisals.AsNoTracking().Where(a => a.Status != AppraisalStatus.Draft);
        if (!actor.CanManage) query = query.Where(a => a.ReviewerEmployeeId != null && a.ReviewerEmployeeId == actor.EmployeeId);
        if (cycleId is int c) query = query.Where(a => a.CycleId == c);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(a => a.Status == status);
        return await query
            .OrderBy(a => a.Status == AppraisalStatus.Reviewed).ThenByDescending(a => a.SubmittedAt)
            .Select(a => new ReviewListItemDto(a.AppraisalId, a.CycleId, a.Cycle.Name, a.EmployeeId,
                a.Employee.FirstName + " " + a.Employee.LastName, a.Employee.Designation, a.Status, a.SubmittedAt, a.FinalRating))
            .ToListAsync(ct);
    }

    public async Task<AppraisalFormDto> GetForReviewAsync(int appraisalId, AppraisalActor actor, CancellationToken ct)
    {
        var appraisal = await LoadAppraisalAsync(a => a.AppraisalId == appraisalId, tracking: false, ct)
                        ?? throw new NotFoundException("Appraisal not found.");
        EnsureReviewer(appraisal, actor);
        var cycle = await db.AppraisalCycles.AsNoTracking().Include(c => c.Questions).FirstAsync(c => c.CycleId == appraisal.CycleId, ct);
        return await ToFormAsync(cycle, appraisal.EmployeeId, appraisal, asReviewer: true,
            canReview: appraisal.Status == AppraisalStatus.Submitted && appraisal.EmployeeId != actor.EmployeeId &&
                       (actor.CanReviewAll || appraisal.ReviewerEmployeeId == actor.EmployeeId), ct);
    }

    public async Task<AppraisalFormDto> SaveReviewAsync(int appraisalId, AppraisalActor actor, ReviewRequest req, CancellationToken ct)
    {
        var appraisal = await LoadAppraisalAsync(a => a.AppraisalId == appraisalId, tracking: true, ct)
                        ?? throw new NotFoundException("Appraisal not found.");
        EnsureReviewer(appraisal, actor, edit: true);
        if (appraisal.EmployeeId == actor.EmployeeId) throw new ForbiddenException("You cannot review your own appraisal.");
        if (appraisal.Status != AppraisalStatus.Submitted)
            throw new ConflictException(appraisal.Status == AppraisalStatus.Reviewed ? "This appraisal has already been reviewed." : "The employee has not submitted this appraisal yet.");

        var cycle = await db.AppraisalCycles.AsNoTracking().Include(c => c.Questions).FirstAsync(c => c.CycleId == appraisal.CycleId, ct);
        var questions = cycle.Questions.ToDictionary(q => q.QuestionId);
        foreach (var input in req.Answers)
        {
            if (!questions.TryGetValue(input.QuestionId, out var q)) throw new AppException("Unknown question in the form.");
            var answer = appraisal.Answers.FirstOrDefault(x => x.QuestionId == q.QuestionId);
            if (answer is null) appraisal.Answers.Add(answer = new AppraisalAnswer { QuestionId = q.QuestionId });
            answer.ReviewerRating = q.Kind == QuestionKind.Rating ? (byte?)input.Rating : null;
            answer.ReviewerComment = Clean(input.Comment);
        }
        appraisal.ReviewerSummary = Clean(req.ReviewerSummary);
        appraisal.FinalRating = req.FinalRating is decimal f ? Math.Round(f, 1) : null;
        appraisal.RecommendedHikePercent = req.RecommendedHikePercent;
        appraisal.UpdatedAt = time.GetUtcNow().UtcDateTime;

        if (req.Complete)
        {
            var missing = cycle.Questions.Where(q => q.Kind == QuestionKind.Rating &&
                                                     appraisal.Answers.FirstOrDefault(a => a.QuestionId == q.QuestionId)?.ReviewerRating is null)
                .Select(q => q.SortOrder).ToList();
            if (missing.Count > 0) throw new AppException($"Please rate every rating question (question {string.Join(", ", missing)}).");
            if (appraisal.FinalRating is null) throw new AppException("Please give a final rating.");

            appraisal.Status = AppraisalStatus.Reviewed;
            appraisal.ReviewedAt = time.GetUtcNow().UtcDateTime;
            appraisal.ReviewedByUserId = actor.UserId;
            var emp = await db.Employees.AsNoTracking().Where(e => e.EmployeeId == appraisal.EmployeeId)
                .Select(e => new { e.FirstName, e.LastName, e.Email }).FirstAsync(ct);
            mail.Enqueue(emp.Email, $"{emp.FirstName} {emp.LastName}", $"{cycle.Name}: your appraisal review is ready",
                Layout(await CompanyNameAsync(ct), "Your appraisal has been reviewed",
                    P($"Dear {emp.FirstName},") +
                    P($"Your {cycle.Name} appraisal has been reviewed. Your final rating is {appraisal.FinalRating:0.0} / 5.") +
                    P("You can read your reviewer's comments on each question in the HR portal."),
                    "View my review", $"{mail.AppUrl}/me/appraisal/{cycle.CycleId}"),
                Category);
        }

        await db.SaveChangesAsync(ct);
        return await GetForReviewAsync(appraisalId, actor, ct);
    }

    // ================================================================== Helpers
    private async Task QueueSubmittedEmailsAsync(AppraisalCycle cycle, Appraisal appraisal, string name, string firstName, string email,
        int? managerId, CancellationToken ct)
    {
        var company = await CompanyNameAsync(ct);
        mail.Enqueue(email, name, $"{cycle.Name}: self-appraisal submitted",
            Layout(company, "Self-appraisal submitted",
                P($"Dear {firstName},") +
                P($"Thank you - your self-appraisal for {cycle.Name} has been submitted for review. You will get an email when the review is complete.")),
            Category);

        // Reviewer: the reporting manager, otherwise everyone who manages appraisals.
        var reviewers = managerId is int mid
            ? await db.Employees.Where(e => e.EmployeeId == mid && e.IsActive)
                .Select(e => new ManagerContact(e.Email, e.FirstName + " " + e.LastName)).ToListAsync(ct)
            : await AppraisalManagersQuery().ToListAsync(ct);
        // A brand-new appraisal has no id until saved, so link to the review list in that case.
        var link = appraisal.AppraisalId > 0 ? $"{mail.AppUrl}/appraisals/review/{appraisal.AppraisalId}" : $"{mail.AppUrl}/appraisals/reviews";
        foreach (var r in reviewers)
        {
            mail.Enqueue(r.Email, r.Name, $"{cycle.Name}: {name} submitted their self-appraisal",
                Layout(company, "An appraisal is waiting for your review",
                    P($"Dear {r.Name.Split(' ')[0]},") +
                    P($"{name} has submitted their self-appraisal for {cycle.Name}.") +
                    (cycle.ReviewDueOn is DateOnly due ? P($"Please complete the review by {due:dd MMM yyyy}.") : ""),
                    "Review appraisal", link),
                Category);
        }
    }

    /// <summary>Active employees whose login has appraisals.edit (Admin role always has it).</summary>
    private IQueryable<ManagerContact> AppraisalManagersQuery() =>
        db.Users.Where(u => u.IsActive && u.Employee != null && u.Employee.IsActive &&
                            (u.RoleId == Roles.AdminId || u.Role.Permissions.Any(p => p.Permission == Permissions.AppraisalsEdit)))
            .Select(u => new ManagerContact(u.Employee!.Email, u.Employee.FirstName + " " + u.Employee.LastName));

    private sealed record ManagerContact(string Email, string Name);

    private IQueryable<Employee> EligibleQuery(AppraisalCycle cycle) =>
        db.Employees.AsNoTracking().Where(e => e.IsActive && e.DateOfJoining <= cycle.PeriodTo);

    private async Task EnsureEligibleAsync(AppraisalCycle cycle, int employeeId, CancellationToken ct)
    {
        var joined = await db.Employees.Where(e => e.EmployeeId == employeeId).Select(e => (DateOnly?)e.DateOfJoining).FirstOrDefaultAsync(ct);
        if (joined is null || joined > cycle.PeriodTo) throw new NotFoundException("This appraisal cycle does not apply to you.");
    }

    private static void EnsureReviewer(Appraisal a, AppraisalActor actor, bool edit = false)
    {
        if (edit ? actor.CanReviewAll : actor.CanManage) return;
        if (a.ReviewerEmployeeId is not null && a.ReviewerEmployeeId == actor.EmployeeId) return;
        throw new ForbiddenException("You are not the reviewer of this appraisal.");
    }

    private async Task<Appraisal?> LoadAppraisalAsync(System.Linq.Expressions.Expression<Func<Appraisal, bool>> filter, bool tracking, CancellationToken ct)
    {
        var q = db.Appraisals.Include(a => a.Answers).Include(a => a.Reviewer).Where(filter);
        return await (tracking ? q : q.AsNoTracking()).FirstOrDefaultAsync(ct);
    }

    private async Task<AppraisalCycle> LoadCycleAsync(int id, CancellationToken ct) =>
        await db.AppraisalCycles.FirstOrDefaultAsync(c => c.CycleId == id, ct) ?? throw new NotFoundException("Appraisal cycle not found.");

    private async Task<AppraisalFormDto> ToFormAsync(AppraisalCycle cycle, int employeeId, Appraisal? a, bool asReviewer, bool canReview,
        CancellationToken ct)
    {
        var emp = await db.Employees.AsNoTracking().Where(e => e.EmployeeId == employeeId)
            .Select(e => new { Name = e.FirstName + " " + e.LastName, e.EmployeeCode, e.Designation, Dept = e.Department.Name, ManagerName = e.Manager != null ? e.Manager.FirstName + " " + e.Manager.LastName : null })
            .FirstAsync(ct);
        var status = a?.Status ?? AppraisalStatus.NotStarted;
        var state = StateOf(cycle, Today);
        var showReview = asReviewer || status == AppraisalStatus.Reviewed;

        var answers = (a?.Answers ?? []).Select(x => new AppraisalAnswerDto(x.QuestionId, x.SelfRating, x.SelfComment,
            showReview ? x.ReviewerRating : null, showReview ? x.ReviewerComment : null)).ToList();
        var reviewerName = a?.Reviewer is { } r ? $"{r.FirstName} {r.LastName}" : emp.ManagerName;

        return new AppraisalFormDto(a?.AppraisalId, cycle.CycleId, cycle.Name, cycle.Description, cycle.PeriodFrom, cycle.PeriodTo,
            cycle.OpensOn, cycle.ClosesOn, state, employeeId, emp.Name, emp.EmployeeCode, emp.Designation, emp.Dept, status,
            CanEditSelf: !asReviewer && state == "Open" && status is AppraisalStatus.NotStarted or AppraisalStatus.Draft,
            CanReview: canReview, ShowReview: showReview,
            cycle.Questions.OrderBy(q => q.SortOrder).Select(ToDto).ToList(), answers,
            a?.SelfSummary, showReview ? a?.ReviewerSummary : null, showReview ? a?.FinalRating : null,
            asReviewer ? a?.RecommendedHikePercent : null, reviewerName, a?.SubmittedAt, a?.ReviewedAt,
            // The reviewer weighs every appreciation / complaint of the period; the employee sees what was shared with them.
            await feedback.ForPeriodAsync(employeeId, cycle.PeriodFrom, cycle.PeriodTo, includeUnshared: asReviewer, ct));
    }

    private async Task<CycleCountsDto> CountsAsync(AppraisalCycle c, CancellationToken ct)
    {
        var eligible = await EligibleQuery(c).CountAsync(ct);
        var byStatus = await db.Appraisals.Where(a => a.CycleId == c.CycleId).GroupBy(a => a.Status)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var started = byStatus.Values.Sum();
        return new CycleCountsDto(eligible, Math.Max(0, eligible - started), byStatus.GetValueOrDefault(AppraisalStatus.Draft),
            byStatus.GetValueOrDefault(AppraisalStatus.Submitted), byStatus.GetValueOrDefault(AppraisalStatus.Reviewed));
    }

    private async Task<string> CompanyNameAsync(CancellationToken ct) =>
        await db.CompanyProfiles.Select(c => c.LegalName ?? c.Name).FirstOrDefaultAsync(ct) ?? "HR Payroll";

    private void ValidateDates(CycleRequest r)
    {
        if (r.PeriodTo < r.PeriodFrom) throw new AppException("Review period 'to' must be on or after 'from'.");
        if (r.ClosesOn < r.OpensOn) throw new AppException("The form must close on or after the day it opens.");
        if (r.ReviewDueOn is DateOnly due && due < r.ClosesOn) throw new AppException("The review due date must be on or after the closing date.");
    }

    private static void Apply(AppraisalCycle c, CycleRequest r)
    {
        c.Name = r.Name.Trim();
        c.Description = string.IsNullOrWhiteSpace(r.Description) ? null : r.Description.Trim();
        c.PeriodFrom = r.PeriodFrom;
        c.PeriodTo = r.PeriodTo;
        c.OpensOn = r.OpensOn;
        c.ClosesOn = r.ClosesOn;
        c.ReviewDueOn = r.ReviewDueOn;
    }

    private static bool Answered(AppraisalQuestion q, AppraisalAnswer? a, bool self) =>
        a is not null && (q.Kind == QuestionKind.Rating
            ? (self ? a.SelfRating : a.ReviewerRating) is not null
            : !string.IsNullOrWhiteSpace(self ? a.SelfComment : a.ReviewerComment));

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static AppraisalQuestionDto ToDto(AppraisalQuestion q) => new(q.QuestionId, q.SortOrder, q.Section, q.Text, q.Kind, q.IsRequired);

    private AppraisalCycleDto ToDto(AppraisalCycle c, CycleCountsDto counts) => new(
        c.CycleId, c.Name, c.Description, c.PeriodFrom, c.PeriodTo, c.OpensOn, c.ClosesOn, c.ReviewDueOn, c.IsPublished,
        StateOf(c, Today), c.Questions.OrderBy(q => q.SortOrder).Select(ToDto).ToList(), counts);

    private static List<AppraisalQuestion> DefaultQuestions()
    {
        (string Section, string Text, string Kind, bool Required)[] items =
        [
            ("Goals & delivery", "What were your key achievements in this period?", QuestionKind.Text, true),
            ("Goals & delivery", "Quality of work", QuestionKind.Rating, true),
            ("Goals & delivery", "Meeting deadlines and commitments", QuestionKind.Rating, true),
            ("Skills & behaviour", "Job knowledge and technical / functional skills", QuestionKind.Rating, true),
            ("Skills & behaviour", "Communication and teamwork", QuestionKind.Rating, true),
            ("Skills & behaviour", "Initiative and ownership", QuestionKind.Rating, true),
            ("Development", "Which areas would you like to improve? What training or support do you need?", QuestionKind.Text, false),
            ("Development", "What are your goals for the next period?", QuestionKind.Text, true),
        ];
        return items.Select((q, i) => new AppraisalQuestion
            { SortOrder = i + 1, Section = q.Section, Text = q.Text, Kind = q.Kind, IsRequired = q.Required }).ToList();
    }
}
