using HrPayroll.BuildingBlocks.Common;
using HrPayroll.Recruitment.Data;
using HrPayroll.Contracts.Dtos;
using HrPayroll.BuildingBlocks.Email;
using HrPayroll.Recruitment.Entities;
using Microsoft.EntityFrameworkCore;
using static HrPayroll.BuildingBlocks.Email.EmailTemplates;

namespace HrPayroll.Recruitment.Services;

/// <summary>Who is acting: recruiters (recruitment.view / .edit) see / act on everything; interviewers only their interviews.</summary>
public record RecruiterActor(int UserId, int? EmployeeId, bool CanManage);

/// <summary>
/// Recruitment (applicant tracking): job openings, candidates, the hiring pipeline, interview scheduling and
/// interviewer feedback, plus a per-candidate activity timeline with follow-ups. Every significant change is
/// logged to the timeline automatically; interview invites / changes are emailed through the outbox.
/// </summary>
public class RecruitmentService(RecruitmentDbContext db, EmailQueue mail, TimeProvider time)
{
    public const int MaxResumeBytes = 5 * 1024 * 1024;
    private const string Category = "Recruitment";

    private DateTime Now => time.GetLocalNow().DateTime;
    private DateOnly Today => DateOnly.FromDateTime(Now);

    // ================================================================== Jobs
    public async Task<List<JobDto>> GetJobsAsync(string? status, CancellationToken ct)
    {
        var query = db.JobOpenings.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(j => j.Status == status);
        var jobs = await ProjectJobs(query.OrderBy(j => j.Status == JobStatus.Closed).ThenByDescending(j => j.CreatedAt)).ToListAsync(ct);
        return await WithStagesAsync(jobs, ct);
    }

    public async Task<JobDto> GetJobAsync(int id, CancellationToken ct)
    {
        var job = await ProjectJobs(db.JobOpenings.AsNoTracking().Where(j => j.JobId == id)).FirstOrDefaultAsync(ct)
                  ?? throw new NotFoundException("Job opening not found.");
        return (await WithStagesAsync([job], ct))[0];
    }

    public async Task<JobDto> SaveJobAsync(int? id, JobRequest req, int userId, CancellationToken ct)
    {
        if (req.ExperienceMax < req.ExperienceMin) throw new AppException("Maximum experience must be at least the minimum.");
        if (req.SalaryMax < req.SalaryMin) throw new AppException("Maximum salary must be at least the minimum.");
        if (!await db.Departments.AnyAsync(d => d.DepartmentId == req.DepartmentId && d.IsActive, ct)) throw new AppException("Invalid department.");
        if (req.HiringManagerEmployeeId is int hm && !await db.Employees.AnyAsync(e => e.EmployeeId == hm && e.IsActive, ct))
            throw new AppException("Hiring manager must be an active employee.");

        var job = id is int existing
            ? await db.JobOpenings.FirstOrDefaultAsync(j => j.JobId == existing, ct) ?? throw new NotFoundException("Job opening not found.")
            : db.JobOpenings.Add(new JobOpening { CreatedByUserId = userId }).Entity;
        job.Title = req.Title.Trim();
        job.DepartmentId = req.DepartmentId;
        job.Location = Clean(req.Location);
        job.EmploymentType = req.EmploymentType;
        job.Openings = req.Openings;
        job.ExperienceMin = req.ExperienceMin;
        job.ExperienceMax = req.ExperienceMax;
        job.SalaryMin = req.SalaryMin;
        job.SalaryMax = req.SalaryMax;
        job.Description = Clean(req.Description);
        job.Skills = Clean(req.Skills);
        job.Status = req.Status;
        job.HiringManagerEmployeeId = req.HiringManagerEmployeeId;
        job.TargetDate = req.TargetDate;
        await db.SaveChangesAsync(ct);
        return await GetJobAsync(job.JobId, ct);
    }

    public Task<List<ApplicationDto>> GetPipelineAsync(int jobId, CancellationToken ct) =>
        ProjectApplications(db.JobApplications.AsNoTracking().Where(a => a.JobId == jobId)
            .OrderByDescending(a => a.StageChangedAt)).ToListAsync(ct);

    // ================================================================== Candidates
    public async Task<PagedResult<CandidateListItemDto>> SearchCandidatesAsync(string? search, string? source, int? jobId, string? stage,
        int page, int pageSize, CancellationToken ct)
    {
        var query = db.Candidates.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            query = query.Where(c => c.FullName.Contains(s) || c.Email.Contains(s) || (c.Phone != null && c.Phone.Contains(s)) ||
                                     (c.Skills != null && c.Skills.Contains(s)) || (c.CurrentCompany != null && c.CurrentCompany.Contains(s)));
        }
        if (!string.IsNullOrWhiteSpace(source)) query = query.Where(c => c.Source == source);
        if (jobId is int j) query = query.Where(c => db.JobApplications.Any(a => a.CandidateId == c.CandidateId && a.JobId == j));
        if (!string.IsNullOrWhiteSpace(stage)) query = query.Where(c => db.JobApplications.Any(a => a.CandidateId == c.CandidateId && a.Stage == stage));

        pageSize = Math.Clamp(pageSize, 1, 100);
        page = Math.Max(page, 1);
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(c => c.CreatedAt).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(c => new CandidateListItemDto(c.CandidateId, c.FullName, c.Email, c.Phone, c.CurrentTitle, c.CurrentCompany,
                c.ExperienceYears, c.Source, db.CandidateResumes.Any(r => r.CandidateId == c.CandidateId), c.CreatedAt,
                db.JobApplications.Where(a => a.CandidateId == c.CandidateId).OrderByDescending(a => a.StageChangedAt).Select(a => a.Job.Title).FirstOrDefault(),
                db.JobApplications.Where(a => a.CandidateId == c.CandidateId).OrderByDescending(a => a.StageChangedAt).Select(a => a.Stage).FirstOrDefault()))
            .ToListAsync(ct);
        return new PagedResult<CandidateListItemDto>(items, total, page, pageSize);
    }

    public async Task<CandidateDetailDto> GetCandidateAsync(int id, CancellationToken ct)
    {
        var c = await db.Candidates.AsNoTracking().Include(x => x.ReferredBy).FirstOrDefaultAsync(x => x.CandidateId == id, ct)
                ?? throw new NotFoundException("Candidate not found.");
        var resume = await db.CandidateResumes.Where(r => r.CandidateId == id).Select(r => r.FileName).FirstOrDefaultAsync(ct);
        var apps = await ProjectApplications(db.JobApplications.AsNoTracking().Where(a => a.CandidateId == id).OrderByDescending(a => a.CreatedAt)).ToListAsync(ct);
        var interviews = await ProjectInterviews(db.Interviews.AsNoTracking().Where(i => i.Application.CandidateId == id)
            .OrderByDescending(i => i.ScheduledAt)).ToListAsync(ct);
        var activities = await ProjectActivities(db.RecruitmentActivities.AsNoTracking().Where(a => a.CandidateId == id)
            .OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.ActivityId)).ToListAsync(ct);

        return new CandidateDetailDto(c.CandidateId, c.FullName, c.Email, c.Phone, c.CurrentCompany, c.CurrentTitle, c.ExperienceYears,
            c.CurrentCtc, c.ExpectedCtc, c.NoticePeriodDays, c.Location, c.Source, c.ReferredByEmployeeId, c.ReferredBy?.FullName,
            c.Skills, c.LinkedInUrl, resume, c.CreatedAt, apps, interviews, activities);
    }

    public async Task<CandidateDetailDto> SaveCandidateAsync(int? id, CandidateRequest req, int userId, CancellationToken ct)
    {
        var email = req.Email.Trim().ToLowerInvariant();
        var duplicate = await db.Candidates.Where(c => c.Email == email && c.CandidateId != id).Select(c => (int?)c.CandidateId).FirstOrDefaultAsync(ct);
        if (duplicate is int dupId) throw new ConflictException($"A candidate with email {email} already exists (candidate #{dupId}).");
        if (req.Source == "Referral" && req.ReferredByEmployeeId is null) throw new AppException("Please choose the employee who referred this candidate.");
        if (req.ReferredByEmployeeId is int refId && !await db.Employees.AnyAsync(e => e.EmployeeId == refId, ct))
            throw new AppException("Referring employee not found.");

        var isNew = id is null;
        var c = id is int existing
            ? await db.Candidates.FirstOrDefaultAsync(x => x.CandidateId == existing, ct) ?? throw new NotFoundException("Candidate not found.")
            : db.Candidates.Add(new Candidate { CreatedByUserId = userId }).Entity;
        c.FullName = req.FullName.Trim();
        c.Email = email;
        c.Phone = Clean(req.Phone);
        c.CurrentCompany = Clean(req.CurrentCompany);
        c.CurrentTitle = Clean(req.CurrentTitle);
        c.ExperienceYears = req.ExperienceYears;
        c.CurrentCtc = req.CurrentCtc;
        c.ExpectedCtc = req.ExpectedCtc;
        c.NoticePeriodDays = req.NoticePeriodDays;
        c.Location = Clean(req.Location);
        c.Source = req.Source;
        c.ReferredByEmployeeId = req.Source == "Referral" ? req.ReferredByEmployeeId : null;
        c.Skills = Clean(req.Skills);
        c.LinkedInUrl = Clean(req.LinkedInUrl);

        if (isNew)
        {
            await db.SaveChangesAsync(ct);
            Log(c.CandidateId, null, ActivityType.Created, $"Candidate added (source: {c.Source})", null, userId);
            if (req.ApplyToJobId is int jobId) await AddApplicationAsync(c.CandidateId, jobId, userId, ct);
        }
        await db.SaveChangesAsync(ct);
        return await GetCandidateAsync(c.CandidateId, ct);
    }

    // ------------------------------------------------------------------ Resume
    public async Task SetResumeAsync(int candidateId, IFormFile file, int userId, CancellationToken ct)
    {
        if (!await db.Candidates.AnyAsync(c => c.CandidateId == candidateId, ct)) throw new NotFoundException("Candidate not found.");
        var bytes = await FileSignatures.ReadAsync(file, MaxResumeBytes, ct);
        var type = FileSignatures.DetectDocument(bytes) ?? throw new AppException("Resume must be a PDF or Word (.docx) file.");
        var resume = await db.CandidateResumes.FirstOrDefaultAsync(r => r.CandidateId == candidateId, ct);
        if (resume is null) db.CandidateResumes.Add(resume = new CandidateResume { CandidateId = candidateId });
        resume.Content = bytes;
        resume.ContentType = type;
        resume.FileName = FileSignatures.SafeFileName(file.FileName, type == "application/pdf" ? ".pdf" : ".docx", "resume");
        resume.UploadedAt = time.GetUtcNow().UtcDateTime;
        Log(candidateId, null, ActivityType.Resume, $"Resume uploaded ({resume.FileName})", null, userId);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Recruiters, or an employee who interviews this candidate.</summary>
    public async Task<(byte[] Bytes, string Type, string Name)> GetResumeAsync(int candidateId, RecruiterActor actor, CancellationToken ct)
    {
        if (!actor.CanManage && !(actor.EmployeeId is int me &&
                                  await db.Interviews.AnyAsync(i => i.Application.CandidateId == candidateId && i.InterviewerEmployeeId == me, ct)))
            throw new ForbiddenException("You can only open resumes of candidates you interview.");
        var r = await db.CandidateResumes.AsNoTracking().FirstOrDefaultAsync(x => x.CandidateId == candidateId, ct)
                ?? throw new NotFoundException("No resume uploaded for this candidate.");
        return (r.Content, r.ContentType, r.FileName);
    }

    // ================================================================== Pipeline
    public async Task<ApplicationDto> ApplyAsync(ApplyRequest req, int userId, CancellationToken ct)
    {
        if (!await db.Candidates.AnyAsync(c => c.CandidateId == req.CandidateId, ct)) throw new NotFoundException("Candidate not found.");
        var app = await AddApplicationAsync(req.CandidateId, req.JobId, userId, ct);
        await db.SaveChangesAsync(ct);
        return await GetApplicationAsync(app.ApplicationId, ct);
    }

    public async Task<ApplicationDto> ChangeStageAsync(int applicationId, StageRequest req, int userId, CancellationToken ct)
    {
        var app = await db.JobApplications.Include(a => a.Candidate).Include(a => a.Job)
                      .FirstOrDefaultAsync(a => a.ApplicationId == applicationId, ct)
                  ?? throw new NotFoundException("Application not found.");
        if (app.Stage == req.Stage) throw new ConflictException($"The application is already at {req.Stage}.");
        if (app.HiredEmployeeId is not null) throw new ConflictException("This candidate has already joined as an employee.");
        if (req.Stage == ApplicationStage.Hired && req.JoiningDate is null) throw new AppException("Please enter the joining date.");
        if (req.Stage == ApplicationStage.Rejected && string.IsNullOrWhiteSpace(req.Reason)) throw new AppException("Please give a reason for rejecting.");

        var from = app.Stage;
        app.Stage = req.Stage;
        app.StageChangedAt = time.GetUtcNow().UtcDateTime;
        if (req.Stage == ApplicationStage.Rejected) app.RejectionReason = req.Reason!.Trim();
        if (req.Stage is ApplicationStage.Offer or ApplicationStage.Hired)
        {
            if (req.OfferedCtc is decimal ctc) app.OfferedCtc = ctc;
            if (req.JoiningDate is DateOnly join) app.JoiningDate = join;
        }

        var details = req.Stage switch
        {
            ApplicationStage.Rejected => $"Reason: {req.Reason!.Trim()}",
            ApplicationStage.Offer or ApplicationStage.Hired =>
                string.Join(" · ", new[] { app.OfferedCtc is decimal o ? $"CTC {o:N0}" : null, app.JoiningDate is DateOnly d ? $"joining {d:dd MMM yyyy}" : null }.Where(x => x != null)),
            _ => Clean(req.Reason),
        };
        Log(app.CandidateId, app.ApplicationId, ActivityType.StageChange, $"{app.Job.Title}: {Label(from)} → {Label(req.Stage)}",
            string.IsNullOrEmpty(details) ? null : details, userId);

        // Leaving the pipeline cancels interviews that have not happened yet.
        if (req.Stage is ApplicationStage.Rejected or ApplicationStage.Withdrawn)
        {
            var upcoming = await db.Interviews.Include(i => i.Interviewer)
                .Where(i => i.ApplicationId == applicationId && i.Status == InterviewStatus.Scheduled).ToListAsync(ct);
            foreach (var i in upcoming)
            {
                i.Status = InterviewStatus.Cancelled;
                QueueInterviewerCancel(i, app.Candidate.FullName, app.Job.Title, $"candidate {Label(req.Stage).ToLowerInvariant()}");
            }
        }

        if (req.Stage == ApplicationStage.Rejected && req.NotifyCandidate)
        {
            mail.Enqueue(app.Candidate.Email, app.Candidate.FullName, $"Your application for {app.Job.Title}",
                Layout(await CompanyNameAsync(ct), "Thank you for your interest",
                    P($"Dear {app.Candidate.FullName},") +
                    P($"Thank you for the time you invested in applying for the {app.Job.Title} position with us. After careful consideration we have decided not to move forward with your application at this time.") +
                    P("We will keep your profile on file and may contact you about future openings that match your experience. We wish you every success.")),
                Category);
            Log(app.CandidateId, app.ApplicationId, ActivityType.Email, "Rejection email sent to candidate", null, userId);
        }

        await db.SaveChangesAsync(ct);
        return await GetApplicationAsync(applicationId, ct);
    }

    /// <summary>Links a hired candidate to the employee record created for them.</summary>
    public async Task<ApplicationDto> LinkEmployeeAsync(int applicationId, int employeeId, int userId, CancellationToken ct)
    {
        var app = await db.JobApplications.FirstOrDefaultAsync(a => a.ApplicationId == applicationId, ct) ?? throw new NotFoundException("Application not found.");
        if (app.Stage != ApplicationStage.Hired) throw new AppException("Only hired candidates can be linked to an employee.");
        var emp = await db.Employees.Where(e => e.EmployeeId == employeeId).Select(e => new { e.EmployeeCode, e.FirstName, e.LastName }).FirstOrDefaultAsync(ct)
                  ?? throw new NotFoundException("Employee not found.");
        app.HiredEmployeeId = employeeId;
        Log(app.CandidateId, app.ApplicationId, ActivityType.StageChange, $"Joined as employee {emp.EmployeeCode} ({emp.FirstName} {emp.LastName})", null, userId);
        await db.SaveChangesAsync(ct);
        return await GetApplicationAsync(applicationId, ct);
    }

    // ================================================================== Interviews
    public Task<List<InterviewDto>> GetInterviewsAsync(DateOnly? from, DateOnly? to, string? status, int? interviewerId, CancellationToken ct)
    {
        var q = db.Interviews.AsNoTracking();
        if (from is DateOnly f) { var start = f.ToDateTime(TimeOnly.MinValue); q = q.Where(i => i.ScheduledAt >= start); }
        if (to is DateOnly t) { var end = t.AddDays(1).ToDateTime(TimeOnly.MinValue); q = q.Where(i => i.ScheduledAt < end); }
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(i => i.Status == status);
        if (interviewerId is int iv) q = q.Where(i => i.InterviewerEmployeeId == iv);
        return ProjectInterviews(q.OrderBy(i => i.ScheduledAt)).ToListAsync(ct);
    }

    public async Task<InterviewDto> ScheduleAsync(InterviewRequest req, int userId, CancellationToken ct)
    {
        var app = await db.JobApplications.Include(a => a.Candidate).Include(a => a.Job)
                      .FirstOrDefaultAsync(a => a.ApplicationId == req.ApplicationId, ct)
                  ?? throw new NotFoundException("Application not found.");
        if (!ApplicationStage.Active.Contains(app.Stage)) throw new AppException($"Cannot schedule an interview - the application is {Label(app.Stage).ToLowerInvariant()}.");
        await ValidateSlotAsync(req, null, ct);

        var interview = new Interview { ApplicationId = app.ApplicationId, CreatedByUserId = userId, Status = InterviewStatus.Scheduled };
        ApplySlot(interview, req);
        db.Interviews.Add(interview);

        if (app.Stage is ApplicationStage.Applied or ApplicationStage.Screening)
        {
            Log(app.CandidateId, app.ApplicationId, ActivityType.StageChange, $"{app.Job.Title}: {Label(app.Stage)} → Interview", null, userId);
            app.Stage = ApplicationStage.Interview;
            app.StageChangedAt = time.GetUtcNow().UtcDateTime;
        }
        var interviewer = await db.Employees.AsNoTracking().FirstAsync(e => e.EmployeeId == req.InterviewerEmployeeId, ct);
        Log(app.CandidateId, app.ApplicationId, ActivityType.Interview,
            $"{interview.Round} scheduled for {interview.ScheduledAt:dd MMM yyyy, h:mm tt} with {interviewer.FullName}", ModeText(interview), userId);
        await QueueInviteAsync(interview, app, interviewer, req.NotifyCandidate, rescheduled: false, ct);

        await db.SaveChangesAsync(ct);
        return await GetInterviewAsync(interview.InterviewId, ct);
    }

    public async Task<InterviewDto> RescheduleAsync(int id, InterviewRequest req, int userId, CancellationToken ct)
    {
        var interview = await db.Interviews.Include(i => i.Application).ThenInclude(a => a.Candidate)
                            .Include(i => i.Application).ThenInclude(a => a.Job)
                            .FirstOrDefaultAsync(i => i.InterviewId == id, ct)
                        ?? throw new NotFoundException("Interview not found.");
        if (interview.Status != InterviewStatus.Scheduled) throw new ConflictException("Only scheduled interviews can be changed.");
        req.ApplicationId = interview.ApplicationId;
        await ValidateSlotAsync(req, id, ct);
        ApplySlot(interview, req);
        var interviewer = await db.Employees.AsNoTracking().FirstAsync(e => e.EmployeeId == req.InterviewerEmployeeId, ct);
        Log(interview.Application.CandidateId, interview.ApplicationId, ActivityType.Interview,
            $"{interview.Round} rescheduled to {interview.ScheduledAt:dd MMM yyyy, h:mm tt} with {interviewer.FullName}", ModeText(interview), userId);
        await QueueInviteAsync(interview, interview.Application, interviewer, req.NotifyCandidate, rescheduled: true, ct);
        await db.SaveChangesAsync(ct);
        return await GetInterviewAsync(id, ct);
    }

    public async Task<InterviewDto> CancelInterviewAsync(int id, CancelInterviewRequest req, int userId, CancellationToken ct)
    {
        var interview = await db.Interviews.Include(i => i.Interviewer).Include(i => i.Application).ThenInclude(a => a.Candidate)
                            .Include(i => i.Application).ThenInclude(a => a.Job)
                            .FirstOrDefaultAsync(i => i.InterviewId == id, ct)
                        ?? throw new NotFoundException("Interview not found.");
        if (interview.Status != InterviewStatus.Scheduled) throw new ConflictException("Only scheduled interviews can be cancelled.");
        interview.Status = InterviewStatus.Cancelled;
        var app = interview.Application;
        Log(app.CandidateId, app.ApplicationId, ActivityType.Interview, $"{interview.Round} on {interview.ScheduledAt:dd MMM} cancelled", Clean(req.Reason), userId);
        QueueInterviewerCancel(interview, app.Candidate.FullName, app.Job.Title, Clean(req.Reason));
        if (req.NotifyCandidate)
        {
            mail.Enqueue(app.Candidate.Email, app.Candidate.FullName, $"Interview cancelled: {app.Job.Title}",
                Layout(await CompanyNameAsync(ct), "Your interview has been cancelled",
                    P($"Dear {app.Candidate.FullName},") +
                    P($"Your {interview.Round} interview for the {app.Job.Title} position on {interview.ScheduledAt:dddd, dd MMM yyyy 'at' h:mm tt} has been cancelled.") +
                    P("Our recruitment team will contact you about the next steps. We apologise for any inconvenience.")),
                Category);
        }
        await db.SaveChangesAsync(ct);
        return await GetInterviewAsync(id, ct);
    }

    /// <summary>Feedback from the interviewer (or a recruiter on their behalf).</summary>
    public async Task<InterviewDto> SubmitFeedbackAsync(int id, FeedbackRequest req, RecruiterActor actor, CancellationToken ct)
    {
        var interview = await db.Interviews.Include(i => i.Application).ThenInclude(a => a.Job)
                            .FirstOrDefaultAsync(i => i.InterviewId == id, ct)
                        ?? throw new NotFoundException("Interview not found.");
        if (!actor.CanManage && interview.InterviewerEmployeeId != actor.EmployeeId)
            throw new ForbiddenException("Only the interviewer can give feedback for this interview.");
        if (interview.Status == InterviewStatus.Cancelled) throw new ConflictException("This interview was cancelled.");
        if (interview.ScheduledAt > Now) throw new AppException("Feedback can be given once the interview has started.");

        if (req.NoShow)
        {
            interview.Status = InterviewStatus.NoShow;
            interview.Rating = null;
            interview.Recommendation = null;
            interview.Feedback = Clean(req.Feedback);
        }
        else
        {
            if (req.Rating is null || req.Recommendation is null || (req.Feedback?.Trim().Length ?? 0) < 10)
                throw new AppException("Please give a rating, a recommendation and at least a sentence of feedback.");
            interview.Status = InterviewStatus.Completed;
            interview.Rating = (byte)req.Rating.Value;
            interview.Recommendation = req.Recommendation;
            interview.Feedback = req.Feedback!.Trim();
        }
        interview.FeedbackAt = time.GetUtcNow().UtcDateTime;

        var who = await db.Employees.Where(e => e.EmployeeId == interview.InterviewerEmployeeId).Select(e => e.FirstName + " " + e.LastName).FirstAsync(ct);
        Log(interview.Application.CandidateId, interview.ApplicationId, ActivityType.Feedback,
            req.NoShow ? $"{interview.Round}: candidate did not show up"
                       : $"{interview.Round} feedback from {who}: {req.Rating}/5, {RecommendationText(req.Recommendation!)}",
            interview.Feedback, actor.UserId);
        await db.SaveChangesAsync(ct);
        return await GetInterviewAsync(id, ct);
    }

    // ================================================================== Activity
    public Task<List<ActivityDto>> GetActivitiesAsync(int? candidateId, bool followUpsOnly, int take, CancellationToken ct)
    {
        var q = db.RecruitmentActivities.AsNoTracking();
        if (candidateId is int c) q = q.Where(a => a.CandidateId == c);
        if (followUpsOnly) q = q.Where(a => a.FollowUpOn != null && !a.IsDone);
        var ordered = followUpsOnly ? q.OrderBy(a => a.FollowUpOn) : q.OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.ActivityId);
        return ProjectActivities(ordered.Take(Math.Clamp(take, 1, 200))).ToListAsync(ct);
    }

    public async Task<ActivityDto> AddActivityAsync(ActivityRequest req, int userId, CancellationToken ct)
    {
        if (!await db.Candidates.AnyAsync(c => c.CandidateId == req.CandidateId, ct)) throw new NotFoundException("Candidate not found.");
        if (req.ApplicationId is int appId && !await db.JobApplications.AnyAsync(a => a.ApplicationId == appId && a.CandidateId == req.CandidateId, ct))
            throw new AppException("That application does not belong to this candidate.");
        if (req.FollowUpOn is DateOnly f && f < Today) throw new AppException("Follow-up date cannot be in the past.");
        var a = Log(req.CandidateId, req.ApplicationId, req.Type, req.Summary.Trim(), Clean(req.Details), userId);
        a.FollowUpOn = req.FollowUpOn;
        await db.SaveChangesAsync(ct);
        return await ProjectActivities(db.RecruitmentActivities.Where(x => x.ActivityId == a.ActivityId)).FirstAsync(ct);
    }

    public async Task<ActivityDto> SetDoneAsync(int id, bool done, CancellationToken ct)
    {
        var a = await db.RecruitmentActivities.FirstOrDefaultAsync(x => x.ActivityId == id, ct) ?? throw new NotFoundException("Activity not found.");
        if (a.FollowUpOn is null) throw new AppException("Only follow-ups can be marked done.");
        a.IsDone = done;
        await db.SaveChangesAsync(ct);
        return await ProjectActivities(db.RecruitmentActivities.Where(x => x.ActivityId == id)).FirstAsync(ct);
    }

    /// <summary>Only manual entries (notes, calls ...) can be deleted - the automatic history is kept.</summary>
    public async Task DeleteActivityAsync(int id, CancellationToken ct)
    {
        var a = await db.RecruitmentActivities.FirstOrDefaultAsync(x => x.ActivityId == id, ct) ?? throw new NotFoundException("Activity not found.");
        if (!ActivityType.Manual.Contains(a.Type)) throw new AppException("Automatic history entries cannot be deleted.");
        db.RecruitmentActivities.Remove(a);
        await db.SaveChangesAsync(ct);
    }

    // ================================================================== Dashboard
    public async Task<RecruitmentDashboardDto> GetDashboardAsync(CancellationToken ct)
    {
        var now = Now;
        var weekStart = now.Date.AddDays(-(((int)now.DayOfWeek + 6) % 7));
        var monthStartUtc = new DateTime(now.Year, now.Month, 1).ToUniversalTime();
        var openJobs = db.JobOpenings.Where(j => j.Status == JobStatus.Open);

        var pipeline = await db.JobApplications.Where(a => ApplicationStage.Active.Contains(a.Stage))
            .GroupBy(a => a.Stage).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        return new RecruitmentDashboardDto(
            await openJobs.CountAsync(ct),
            await openJobs.SumAsync(j => (int?)j.Openings, ct) ?? 0,
            pipeline.Values.Sum(),
            await db.Interviews.CountAsync(i => i.Status != InterviewStatus.Cancelled && i.ScheduledAt >= weekStart && i.ScheduledAt < weekStart.AddDays(7), ct),
            pipeline.GetValueOrDefault(ApplicationStage.Offer),
            await db.JobApplications.CountAsync(a => a.Stage == ApplicationStage.Hired && a.StageChangedAt >= monthStartUtc, ct),
            ApplicationStage.Active.Select(s => new StageCountDto(s, pipeline.GetValueOrDefault(s))).ToList(),
            await ProjectInterviews(db.Interviews.Where(i => i.Status == InterviewStatus.Scheduled && i.ScheduledAt >= now.Date)
                .OrderBy(i => i.ScheduledAt).Take(8)).ToListAsync(ct),
            await GetActivitiesAsync(null, followUpsOnly: true, 10, ct),
            await GetActivitiesAsync(null, followUpsOnly: false, 15, ct));
    }

    // ================================================================== Helpers
    private async Task<JobApplication> AddApplicationAsync(int candidateId, int jobId, int userId, CancellationToken ct)
    {
        var job = await db.JobOpenings.FirstOrDefaultAsync(j => j.JobId == jobId, ct) ?? throw new NotFoundException("Job opening not found.");
        if (job.Status != JobStatus.Open) throw new AppException($"{job.Title} is not open for applications ({job.Status}).");
        if (await db.JobApplications.AnyAsync(a => a.JobId == jobId && a.CandidateId == candidateId, ct))
            throw new ConflictException($"This candidate has already applied for {job.Title}.");
        var app = new JobApplication { JobId = jobId, CandidateId = candidateId, Stage = ApplicationStage.Applied, StageChangedAt = time.GetUtcNow().UtcDateTime };
        db.JobApplications.Add(app);
        await db.SaveChangesAsync(ct);
        Log(candidateId, app.ApplicationId, ActivityType.Applied, $"Applied for {job.Title}", null, userId);
        return app;
    }

    private async Task ValidateSlotAsync(InterviewRequest req, int? excludeId, CancellationToken ct)
    {
        if (req.ScheduledAt <= Now) throw new AppException("Interview time must be in the future.");
        if (req.ScheduledAt > Now.AddDays(180)) throw new AppException("Interviews can be scheduled at most 6 months ahead.");
        if (!await db.Employees.AnyAsync(e => e.EmployeeId == req.InterviewerEmployeeId && e.IsActive, ct))
            throw new AppException("The interviewer must be an active employee.");
        var start = req.ScheduledAt;
        var end = req.ScheduledAt.AddMinutes(req.DurationMinutes);
        var clash = await db.Interviews
            .Where(i => i.InterviewerEmployeeId == req.InterviewerEmployeeId && i.Status == InterviewStatus.Scheduled && i.InterviewId != excludeId &&
                        i.ScheduledAt < end && i.ScheduledAt.AddMinutes(i.DurationMinutes) > start)
            .Select(i => new { i.ScheduledAt, i.Application.Candidate.FullName })
            .FirstOrDefaultAsync(ct);
        if (clash is not null)
            throw new ConflictException($"The interviewer already has an interview with {clash.FullName} at {clash.ScheduledAt:h:mm tt} that overlaps this slot.");
    }

    private static void ApplySlot(Interview i, InterviewRequest r)
    {
        i.Round = r.Round.Trim();
        i.ScheduledAt = r.ScheduledAt;
        i.DurationMinutes = r.DurationMinutes;
        i.Mode = r.Mode;
        i.Location = string.IsNullOrWhiteSpace(r.Location) ? null : r.Location.Trim();
        i.InterviewerEmployeeId = r.InterviewerEmployeeId;
    }

    private async Task QueueInviteAsync(Interview i, JobApplication app, Employee interviewer, bool notifyCandidate, bool rescheduled, CancellationToken ct)
    {
        var company = await CompanyNameAsync(ct);
        var when = $"{i.ScheduledAt:dddd, dd MMM yyyy 'at' h:mm tt} ({i.DurationMinutes} minutes)";
        var where = ModeText(i);
        var verb = rescheduled ? "rescheduled" : "scheduled";
        if (notifyCandidate)
        {
            mail.Enqueue(app.Candidate.Email, app.Candidate.FullName, $"Interview {verb}: {app.Job.Title} at {company}",
                Layout(company, rescheduled ? "Your interview has been rescheduled" : "Interview invitation",
                    P($"Dear {app.Candidate.FullName},") +
                    P($"Your {i.Round} interview for the {app.Job.Title} position is {verb} for {when}.") +
                    P(where) +
                    P("If this time does not work for you, please reply to this email so we can find another slot.")),
                Category);
        }
        mail.Enqueue(interviewer.Email, interviewer.FullName, $"Interview {verb}: {app.Candidate.FullName} for {app.Job.Title}",
            Layout(company, $"You are interviewing {app.Candidate.FullName}",
                P($"Dear {interviewer.FirstName},") +
                P($"You are the interviewer for the {i.Round} round with {app.Candidate.FullName} ({app.Job.Title}) on {when}.") +
                P(where) +
                P("You can open the candidate's resume and submit your feedback from My Interviews."),
                "Open My Interviews", $"{mail.AppUrl}/me/interviews"),
            Category);
    }

    private void QueueInterviewerCancel(Interview i, string candidate, string job, string? reason)
    {
        if (i.Interviewer is null) return;
        mail.Enqueue(i.Interviewer.Email, i.Interviewer.FullName, $"Interview cancelled: {candidate} ({job})",
            Layout("HR", "Interview cancelled",
                P($"Dear {i.Interviewer.FirstName},") +
                P($"The {i.Round} interview with {candidate} for {job} on {i.ScheduledAt:dd MMM yyyy 'at' h:mm tt} has been cancelled" +
                  (reason is null ? "." : $" ({reason})."))),
            Category);
    }

    private RecruitmentActivity Log(int candidateId, int? applicationId, string type, string summary, string? details, int? userId)
    {
        var a = new RecruitmentActivity
        {
            CandidateId = candidateId, ApplicationId = applicationId, Type = type,
            Summary = summary.Length > 200 ? summary[..200] : summary, Details = details, CreatedByUserId = userId,
            CreatedAt = time.GetUtcNow().UtcDateTime,
        };
        db.RecruitmentActivities.Add(a);
        return a;
    }

    private async Task<ApplicationDto> GetApplicationAsync(int id, CancellationToken ct) =>
        await ProjectApplications(db.JobApplications.AsNoTracking().Where(a => a.ApplicationId == id)).FirstAsync(ct);

    private async Task<InterviewDto> GetInterviewAsync(int id, CancellationToken ct) =>
        await ProjectInterviews(db.Interviews.AsNoTracking().Where(i => i.InterviewId == id)).FirstAsync(ct);

    private async Task<List<JobDto>> WithStagesAsync(List<JobDto> jobs, CancellationToken ct)
    {
        var ids = jobs.Select(j => j.JobId).ToList();
        var counts = await db.JobApplications.Where(a => ids.Contains(a.JobId))
            .GroupBy(a => new { a.JobId, a.Stage }).Select(g => new { g.Key.JobId, g.Key.Stage, Count = g.Count() }).ToListAsync(ct);
        return jobs.Select(j =>
        {
            var mine = counts.Where(c => c.JobId == j.JobId).ToList();
            return j with
            {
                Applicants = mine.Sum(c => c.Count),
                Active = mine.Where(c => ApplicationStage.Active.Contains(c.Stage)).Sum(c => c.Count),
                Hired = mine.Where(c => c.Stage == ApplicationStage.Hired).Sum(c => c.Count),
                Stages = ApplicationStage.All.Select(s => new StageCountDto(s, mine.FirstOrDefault(c => c.Stage == s)?.Count ?? 0)).ToList(),
            };
        }).ToList();
    }

    private static IQueryable<JobDto> ProjectJobs(IQueryable<JobOpening> q) => q.Select(j => new JobDto(
        j.JobId, j.Title, j.DepartmentId, j.Department.Name, j.Location, j.EmploymentType, j.Openings, j.ExperienceMin, j.ExperienceMax,
        j.SalaryMin, j.SalaryMax, j.Description, j.Skills, j.Status, j.HiringManagerEmployeeId,
        j.HiringManager != null ? j.HiringManager.FirstName + " " + j.HiringManager.LastName : null, j.TargetDate, j.CreatedAt,
        0, 0, 0, Array.Empty<StageCountDto>()));

    private IQueryable<ApplicationDto> ProjectApplications(IQueryable<JobApplication> q) => q.Select(a => new ApplicationDto(
        a.ApplicationId, a.JobId, a.Job.Title, a.Job.Status, a.CandidateId, a.Candidate.FullName, a.Stage, a.StageChangedAt,
        a.RejectionReason, a.OfferedCtc, a.JoiningDate, a.HiredEmployeeId, a.CreatedAt,
        db.Interviews.Count(i => i.ApplicationId == a.ApplicationId && i.Status != InterviewStatus.Cancelled),
        db.Interviews.Where(i => i.ApplicationId == a.ApplicationId && i.Rating != null).Average(i => (decimal?)i.Rating)));

    private IQueryable<InterviewDto> ProjectInterviews(IQueryable<Interview> q) => q.Select(i => new InterviewDto(
        i.InterviewId, i.ApplicationId, i.Application.CandidateId, i.Application.Candidate.FullName, i.Application.Job.Title, i.Round,
        i.ScheduledAt, i.DurationMinutes, i.Mode, i.Location, i.InterviewerEmployeeId, i.Interviewer.FirstName + " " + i.Interviewer.LastName,
        i.Status, i.Rating, i.Recommendation, i.Feedback, i.FeedbackAt,
        db.CandidateResumes.Any(r => r.CandidateId == i.Application.CandidateId)));

    private static IQueryable<ActivityDto> ProjectActivities(IQueryable<RecruitmentActivity> q) => q.Select(a => new ActivityDto(
        a.ActivityId, a.CandidateId, a.Candidate.FullName, a.ApplicationId, a.Application != null ? a.Application.Job.Title : null,
        a.Type, a.Summary, a.Details, a.FollowUpOn, a.IsDone,
        a.CreatedBy != null ? (a.CreatedBy.Employee != null ? a.CreatedBy.Employee.FirstName + " " + a.CreatedBy.Employee.LastName : a.CreatedBy.Username) : null,
        a.CreatedAt));

    private async Task<string> CompanyNameAsync(CancellationToken ct) =>
        await db.CompanyProfiles.Select(c => c.LegalName ?? c.Name).FirstOrDefaultAsync(ct) ?? "HR Payroll";

    private static string ModeText(Interview i) => i.Mode switch
    {
        "InPerson" => $"In person{(i.Location is null ? "" : $" at {i.Location}")}.",
        "Phone" => $"By phone{(i.Location is null ? "" : $" ({i.Location})")}.",
        _ => $"Video call{(i.Location is null ? "" : $": {i.Location}")}",
    };

    private static string Label(string stage) => stage == ApplicationStage.Withdrawn ? "Withdrawn" : stage;

    private static string RecommendationText(string r) => r switch
    {
        "StrongHire" => "strong hire", "Hire" => "hire", "NoHire" => "no hire", _ => "strong no hire",
    };

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
