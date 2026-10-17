using Microsoft.EntityFrameworkCore;

namespace HrPayroll.Notifications.Data;

/// <summary>HrPayroll_Notifications: the email log / queue (from every service, via Kafka) and the SMTP settings.</summary>
public class NotificationsDbContext(DbContextOptions<NotificationsDbContext> options) : ServiceDbContext(options)
{
    public static readonly DirectoryModel Layout = new();

    protected override DirectoryModel Directory => Layout;

    public DbSet<EmailMessage> EmailMessages => Set<EmailMessage>();
    public DbSet<EmailSettings> EmailSettings => Set<EmailSettings>();

    protected override void ConfigureModel(ModelBuilder b)
    {
        b.Entity<EmailSettings>(e =>
        {
            e.ToTable("EmailSettings");
            e.HasKey(x => x.SettingsId);
            e.Property(x => x.SettingsId).ValueGeneratedNever();
            e.Property(x => x.Security).IsUnicode(false).HasMaxLength(20);
        });

        b.Entity<EmailMessage>(e =>
        {
            e.ToTable("EmailMessages");
            e.HasKey(x => x.EmailId);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("SYSUTCDATETIME()");
        });
    }
}
