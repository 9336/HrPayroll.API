using Microsoft.EntityFrameworkCore;

namespace HrPayroll.Performance.Data;

/// <summary>HrPayroll_Performance: appraisal cycles and appraisals, appreciations / complaints, and daily tasks.</summary>
public class PerformanceDbContext(DbContextOptions<PerformanceDbContext> options) : ServiceDbContext(options)
{
    public static readonly DirectoryModel Layout = new();

    protected override DirectoryModel Directory => Layout;

    public DbSet<AppraisalCycle> AppraisalCycles => Set<AppraisalCycle>();
    public DbSet<AppraisalQuestion> AppraisalQuestions => Set<AppraisalQuestion>();
    public DbSet<Appraisal> Appraisals => Set<Appraisal>();
    public DbSet<AppraisalAnswer> AppraisalAnswers => Set<AppraisalAnswer>();
    public DbSet<EmployeeFeedback> EmployeeFeedback => Set<EmployeeFeedback>();
    public DbSet<FeedbackAttachment> FeedbackAttachments => Set<FeedbackAttachment>();
    public DbSet<WorkTask> WorkTasks => Set<WorkTask>();
    public DbSet<WorkTaskUpdate> WorkTaskUpdates => Set<WorkTaskUpdate>();

    protected override void ConfigureModel(ModelBuilder b)
    {
        b.Entity<AppraisalCycle>(e =>
        {
            e.ToTable("AppraisalCycles");
            e.HasKey(x => x.CycleId);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("SYSUTCDATETIME()");
            e.HasMany(x => x.Questions).WithOne().HasForeignKey(x => x.CycleId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<AppraisalQuestion>(e =>
        {
            e.ToTable("AppraisalQuestions");
            e.HasKey(x => x.QuestionId);
        });

        b.Entity<Appraisal>(e =>
        {
            e.ToTable("Appraisals");
            e.HasKey(x => x.AppraisalId);
            e.Property(x => x.FinalRating).HasPrecision(3, 1);
            e.Property(x => x.RecommendedHikePercent).HasPrecision(5, 2);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("SYSUTCDATETIME()");
            e.HasOne(x => x.Cycle).WithMany().HasForeignKey(x => x.CycleId);
            e.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId);
            e.HasOne(x => x.Reviewer).WithMany().HasForeignKey(x => x.ReviewerEmployeeId);
            e.HasOne(x => x.ReviewedBy).WithMany().HasForeignKey(x => x.ReviewedByUserId);
            e.HasMany(x => x.Answers).WithOne().HasForeignKey(x => x.AppraisalId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<AppraisalAnswer>(e =>
        {
            e.ToTable("AppraisalAnswers");
            e.HasKey(x => new { x.AppraisalId, x.QuestionId });
        });

        b.Entity<EmployeeFeedback>(e =>
        {
            e.ToTable("EmployeeFeedback");
            e.HasKey(x => x.FeedbackId);
            e.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId);
            e.HasOne(x => x.RecordedBy).WithMany().HasForeignKey(x => x.RecordedByUserId);
            e.HasOne(x => x.ResolvedBy).WithMany().HasForeignKey(x => x.ResolvedByUserId);
            e.HasOne(x => x.Attachment).WithOne().HasForeignKey<FeedbackAttachment>(x => x.FeedbackId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<FeedbackAttachment>(e =>
        {
            e.ToTable("FeedbackAttachments");
            e.HasKey(x => x.FeedbackId);
            e.Property(x => x.FeedbackId).ValueGeneratedNever();
            e.Property(x => x.ContentType).IsUnicode(false).HasMaxLength(100);
        });

        b.Entity<WorkTask>(e =>
        {
            e.ToTable("WorkTasks");
            e.HasKey(x => x.TaskId);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("SYSUTCDATETIME()");
            e.HasOne(x => x.Assignee).WithMany().HasForeignKey(x => x.AssigneeEmployeeId);
            e.HasOne(x => x.AssignedBy).WithMany().HasForeignKey(x => x.AssignedByUserId);
        });

        b.Entity<WorkTaskUpdate>(e =>
        {
            e.ToTable("WorkTaskUpdates");
            e.HasKey(x => x.UpdateId);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("SYSUTCDATETIME()");
            e.HasOne<WorkTask>().WithMany().HasForeignKey(x => x.TaskId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.UpdatedBy).WithMany().HasForeignKey(x => x.UpdatedByUserId);
        });
    }
}
