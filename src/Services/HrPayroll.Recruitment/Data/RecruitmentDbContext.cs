using Microsoft.EntityFrameworkCore;

namespace HrPayroll.Recruitment.Data;

/// <summary>
/// HrPayroll_Recruitment: jobs, candidates and resumes, applications, interviews, the activity timeline, question banks
/// and candidates' online tests.
/// </summary>
public class RecruitmentDbContext(DbContextOptions<RecruitmentDbContext> options) : ServiceDbContext(options)
{
    public static readonly DirectoryModel Layout = new();

    protected override DirectoryModel Directory => Layout;

    public DbSet<JobOpening> JobOpenings => Set<JobOpening>();
    public DbSet<Candidate> Candidates => Set<Candidate>();
    public DbSet<CandidateResume> CandidateResumes => Set<CandidateResume>();
    public DbSet<JobApplication> JobApplications => Set<JobApplication>();
    public DbSet<Interview> Interviews => Set<Interview>();
    public DbSet<RecruitmentActivity> RecruitmentActivities => Set<RecruitmentActivity>();
    public DbSet<QuestionBank> QuestionBanks => Set<QuestionBank>();
    public DbSet<BankQuestion> BankQuestions => Set<BankQuestion>();
    public DbSet<CandidateTest> CandidateTests => Set<CandidateTest>();
    public DbSet<CandidateTestQuestion> CandidateTestQuestions => Set<CandidateTestQuestion>();

    protected override void ConfigureModel(ModelBuilder b)
    {
        b.Entity<JobOpening>(e =>
        {
            e.ToTable("JobOpenings");
            e.HasKey(x => x.JobId);
            e.Property(x => x.ExperienceMin).HasPrecision(4, 1);
            e.Property(x => x.ExperienceMax).HasPrecision(4, 1);
            e.Property(x => x.SalaryMin).HasPrecision(12, 2);
            e.Property(x => x.SalaryMax).HasPrecision(12, 2);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("SYSUTCDATETIME()");
            e.HasOne(x => x.Department).WithMany().HasForeignKey(x => x.DepartmentId);
            e.HasOne(x => x.HiringManager).WithMany().HasForeignKey(x => x.HiringManagerEmployeeId);
        });

        b.Entity<Candidate>(e =>
        {
            e.ToTable("Candidates");
            e.Property(x => x.ExperienceYears).HasPrecision(4, 1);
            e.Property(x => x.CurrentCtc).HasPrecision(12, 2);
            e.Property(x => x.ExpectedCtc).HasPrecision(12, 2);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("SYSUTCDATETIME()");
            e.HasOne(x => x.ReferredBy).WithMany().HasForeignKey(x => x.ReferredByEmployeeId);
        });

        b.Entity<CandidateResume>(e =>
        {
            e.ToTable("CandidateResumes");
            e.HasKey(x => x.CandidateId);
            e.Property(x => x.CandidateId).ValueGeneratedNever();
        });

        b.Entity<JobApplication>(e =>
        {
            e.ToTable("JobApplications");
            e.HasKey(x => x.ApplicationId);
            e.Property(x => x.OfferedCtc).HasPrecision(12, 2);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("SYSUTCDATETIME()");
            e.HasOne(x => x.Job).WithMany().HasForeignKey(x => x.JobId);
            e.HasOne(x => x.Candidate).WithMany().HasForeignKey(x => x.CandidateId);
            e.HasOne(x => x.HiredEmployee).WithMany().HasForeignKey(x => x.HiredEmployeeId);
        });

        b.Entity<Interview>(e =>
        {
            e.ToTable("Interviews");
            e.Property(x => x.CreatedAt).HasDefaultValueSql("SYSUTCDATETIME()");
            e.HasOne(x => x.Application).WithMany().HasForeignKey(x => x.ApplicationId);
            e.HasOne(x => x.Interviewer).WithMany().HasForeignKey(x => x.InterviewerEmployeeId);
        });

        b.Entity<RecruitmentActivity>(e =>
        {
            e.ToTable("RecruitmentActivities");
            e.HasKey(x => x.ActivityId);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("SYSUTCDATETIME()");
            e.HasOne(x => x.Candidate).WithMany().HasForeignKey(x => x.CandidateId);
            e.HasOne(x => x.Application).WithMany().HasForeignKey(x => x.ApplicationId);
            e.HasOne(x => x.CreatedBy).WithMany().HasForeignKey(x => x.CreatedByUserId);
        });

        b.Entity<QuestionBank>(e =>
        {
            e.ToTable("QuestionBanks");
            e.HasKey(x => x.BankId);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("SYSUTCDATETIME()");
            e.HasOne(x => x.CreatedBy).WithMany().HasForeignKey(x => x.CreatedByUserId);
        });

        b.Entity<BankQuestion>(e =>
        {
            e.ToTable("BankQuestions");
            e.HasKey(x => x.QuestionId);
            e.Property(x => x.CorrectOption).IsFixedLength().IsUnicode(false).HasMaxLength(1);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("SYSUTCDATETIME()");
            e.HasOne<QuestionBank>().WithMany().HasForeignKey(x => x.BankId);
        });

        b.Entity<CandidateTest>(e =>
        {
            e.ToTable("CandidateTests");
            e.HasKey(x => x.TestId);
            e.Property(x => x.Token).IsFixedLength().IsUnicode(false).HasMaxLength(43);
            e.Property(x => x.ScorePercent).HasPrecision(5, 2);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("SYSUTCDATETIME()");
            e.HasOne(x => x.Application).WithMany().HasForeignKey(x => x.ApplicationId);
            e.HasOne(x => x.Bank).WithMany().HasForeignKey(x => x.BankId);
            e.HasMany(x => x.Questions).WithOne().HasForeignKey(x => x.TestId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<CandidateTestQuestion>(e =>
        {
            e.ToTable("CandidateTestQuestions");
            e.HasKey(x => new { x.TestId, x.QuestionId });
            e.Property(x => x.SelectedOption).IsFixedLength().IsUnicode(false).HasMaxLength(1);
            e.HasOne(x => x.Question).WithMany().HasForeignKey(x => x.QuestionId);
        });
    }
}
