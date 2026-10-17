using Microsoft.EntityFrameworkCore;

namespace HrPayroll.Chat.Data;

/// <summary>
/// HrPayroll_Chat: conversations, members, messages and attachments. The copy of the logins also holds "last seen"
/// (written by the chat hub; never overwritten by the Identity service's events).
/// </summary>
public class ChatDbContext(DbContextOptions<ChatDbContext> options) : ServiceDbContext(options)
{
    public static readonly DirectoryModel Layout = new();

    protected override DirectoryModel Directory => Layout;

    public DbSet<ChatConversation> ChatConversations => Set<ChatConversation>();
    public DbSet<ChatMember> ChatMembers => Set<ChatMember>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();
    public DbSet<ChatAttachment> ChatAttachments => Set<ChatAttachment>();

    protected override void ConfigureModel(ModelBuilder b)
    {
        b.Entity<ChatConversation>(e =>
        {
            e.ToTable("ChatConversations");
            e.HasKey(x => x.ConversationId);
            e.Property(x => x.DirectKey).IsUnicode(false).HasMaxLength(30);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("SYSUTCDATETIME()");
            e.HasMany(x => x.Members).WithOne().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ChatMember>(e =>
        {
            e.ToTable("ChatMembers");
            e.HasKey(x => new { x.ConversationId, x.UserId });
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId);
        });

        b.Entity<ChatMessage>(e =>
        {
            e.ToTable("ChatMessages");
            e.HasKey(x => x.MessageId);
            e.HasOne<ChatConversation>().WithMany().HasForeignKey(x => x.ConversationId);
            e.HasOne(x => x.Sender).WithMany().HasForeignKey(x => x.SenderUserId);
            e.HasOne(x => x.Attachment).WithOne().HasForeignKey<ChatAttachment>(x => x.MessageId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ChatAttachment>(e =>
        {
            e.ToTable("ChatAttachments");
            e.HasKey(x => x.MessageId);
            e.Property(x => x.MessageId).ValueGeneratedNever();
            e.Property(x => x.FileName).HasMaxLength(120);
            e.Property(x => x.ContentType).IsUnicode(false).HasMaxLength(120);
        });
    }
}
