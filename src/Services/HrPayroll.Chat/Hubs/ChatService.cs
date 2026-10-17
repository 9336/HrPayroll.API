using HrPayroll.BuildingBlocks.Common;
using HrPayroll.Chat.Data;
using HrPayroll.Contracts.Dtos;
using HrPayroll.Chat.Entities;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace HrPayroll.Chat.Hubs;

/// <summary>
/// One-to-one and group chat between users. Every change is saved first and then pushed to the members'
/// SignalR groups (events: conversation, conversationRemoved, message, messageDeleted, read).
/// </summary>
public class ChatService(ChatDbContext db, PresenceTracker presence, IHubContext<ChatHub> hub, TimeProvider time)
{
    private DateTime UtcNow => time.GetUtcNow().UtcDateTime;

    // ------------------------------------------------------------------ Contacts & presence
    public async Task<List<ChatContactDto>> GetContactsAsync(int me, CancellationToken ct)
    {
        var users = await db.Users.AsNoTracking()
            .Where(u => u.UserId != me && u.IsActive && (u.Employee == null || u.Employee.IsActive))
            .Select(u => new
            {
                u.UserId, Name = u.Employee != null ? u.Employee.FirstName + " " + u.Employee.LastName : u.Username,
                Designation = u.Employee != null ? u.Employee.Designation : null,
                Department = u.Employee != null ? u.Employee.Department.Name : null, u.LastSeenAt,
            })
            .ToListAsync(ct);
        return users.Select(u => new ChatContactDto(u.UserId, u.Name, u.Designation, u.Department, presence.IsOnline(u.UserId), u.LastSeenAt))
            .OrderByDescending(u => u.Online).ThenBy(u => u.Name).ToList();
    }

    public IReadOnlyCollection<int> OnlineUserIds() => presence.OnlineUserIds;

    // ------------------------------------------------------------------ Conversations
    public async Task<List<ChatConversationDto>> GetConversationsAsync(int me, CancellationToken ct)
    {
        var ids = await db.ChatMembers.Where(m => m.UserId == me).Select(m => m.ConversationId).ToListAsync(ct);
        return await BuildAsync(ids, me, ct);
    }

    public async Task<ChatConversationDto> GetOrCreateDirectAsync(int me, int other, CancellationToken ct)
    {
        if (other == me) throw new AppException("You cannot start a chat with yourself.");
        if (!await db.Users.AnyAsync(u => u.UserId == other && u.IsActive, ct)) throw new NotFoundException("User not found.");
        var key = ChatConversation.KeyFor(me, other);
        var existing = await db.ChatConversations.Where(c => c.DirectKey == key).Select(c => (int?)c.ConversationId).FirstOrDefaultAsync(ct);
        if (existing is int id) return await GetOneAsync(id, me, ct);

        var conv = new ChatConversation
        {
            IsGroup = false, DirectKey = key, CreatedByUserId = me, LastMessageAt = UtcNow,
            Members = [new ChatMember { UserId = me, JoinedAt = UtcNow }, new ChatMember { UserId = other, JoinedAt = UtcNow }],
        };
        db.ChatConversations.Add(conv);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)   // the other person created it at the same moment
        {
            db.ChangeTracker.Clear();
            var again = await db.ChatConversations.Where(c => c.DirectKey == key).Select(c => c.ConversationId).FirstAsync(ct);
            return await GetOneAsync(again, me, ct);
        }
        return await GetOneAsync(conv.ConversationId, me, ct);
    }

    public async Task<ChatConversationDto> CreateGroupAsync(int me, CreateGroupRequest req, CancellationToken ct)
    {
        var memberIds = req.MemberUserIds.Where(id => id != me).Distinct().ToList();
        if (memberIds.Count == 0) throw new AppException("Add at least one other person to the group.");
        await EnsureActiveUsersAsync(memberIds, ct);
        var conv = new ChatConversation
        {
            IsGroup = true, Name = req.Name.Trim(), CreatedByUserId = me, LastMessageAt = UtcNow,
            Members = [new ChatMember { UserId = me, IsAdmin = true, JoinedAt = UtcNow },
                       .. memberIds.Select(id => new ChatMember { UserId = id, JoinedAt = UtcNow })],
        };
        db.ChatConversations.Add(conv);
        await db.SaveChangesAsync(ct);
        await PushConversationAsync(conv.ConversationId, ct);
        return await GetOneAsync(conv.ConversationId, me, ct);
    }

    public async Task<ChatConversationDto> RenameGroupAsync(int me, int conversationId, string name, CancellationToken ct)
    {
        var conv = await LoadGroupAsAdminAsync(me, conversationId, ct);
        conv.Name = name.Trim();
        await db.SaveChangesAsync(ct);
        await PushConversationAsync(conversationId, ct);
        return await GetOneAsync(conversationId, me, ct);
    }

    public async Task<ChatConversationDto> AddMembersAsync(int me, int conversationId, List<int> userIds, CancellationToken ct)
    {
        var conv = await LoadGroupAsAdminAsync(me, conversationId, ct);
        var toAdd = userIds.Distinct().Where(id => conv.Members.All(m => m.UserId != id)).ToList();
        if (toAdd.Count == 0) throw new AppException("Those people are already in the group.");
        await EnsureActiveUsersAsync(toAdd, ct);
        conv.Members.AddRange(toAdd.Select(id => new ChatMember { ConversationId = conversationId, UserId = id, JoinedAt = UtcNow }));
        await db.SaveChangesAsync(ct);
        await PushConversationAsync(conversationId, ct);
        return await GetOneAsync(conversationId, me, ct);
    }

    /// <summary>Admins remove anyone; everyone can remove themselves (leave). The last admin leaving promotes someone.</summary>
    public async Task RemoveMemberAsync(int me, int conversationId, int userId, CancellationToken ct)
    {
        var conv = await db.ChatConversations.Include(c => c.Members).FirstOrDefaultAsync(c => c.ConversationId == conversationId, ct)
                   ?? throw new NotFoundException("Conversation not found.");
        if (!conv.IsGroup) throw new AppException("You can only leave group chats.");
        var mine = conv.Members.FirstOrDefault(m => m.UserId == me) ?? throw new ForbiddenException("You are not in this group.");
        if (userId != me && !mine.IsAdmin) throw new ForbiddenException("Only group admins can remove people.");
        var target = conv.Members.FirstOrDefault(m => m.UserId == userId) ?? throw new NotFoundException("That person is not in the group.");

        conv.Members.Remove(target);
        if (target.IsAdmin && conv.Members.Count > 0 && conv.Members.All(m => !m.IsAdmin))
            conv.Members.OrderBy(m => m.JoinedAt).First().IsAdmin = true;
        await db.SaveChangesAsync(ct);

        await hub.Clients.Group(ChatHub.UserGroup(userId)).SendAsync("conversationRemoved", conversationId, ct);
        await PushConversationAsync(conversationId, ct);
    }

    // ------------------------------------------------------------------ Messages
    public async Task<List<ChatMessageDto>> GetMessagesAsync(int me, int conversationId, long? beforeId, int take, CancellationToken ct)
    {
        await EnsureMemberAsync(me, conversationId, ct);
        var q = db.ChatMessages.AsNoTracking().Where(m => m.ConversationId == conversationId);
        if (beforeId is long before) q = q.Where(m => m.MessageId < before);
        var page = await Project(q.OrderByDescending(m => m.MessageId).Take(Math.Clamp(take, 1, 100))).ToListAsync(ct);
        page.Reverse();
        return page;
    }

    public const int MaxAttachmentBytes = 10 * 1024 * 1024;

    public Task<ChatMessageDto> SendAsync(int me, int conversationId, string body, CancellationToken ct) =>
        SendAsync(me, conversationId, body, attachment: null, ct);

    /// <summary>Sends a file, with an optional caption.</summary>
    public async Task<ChatMessageDto> SendFileAsync(int me, int conversationId, IFormFile file, string? caption, CancellationToken ct)
    {
        if ((caption?.Length ?? 0) > 4000) throw new AppException("Caption must be 4000 characters or fewer.");
        await EnsureMemberAsync(me, conversationId, ct);
        var bytes = await FileSignatures.ReadAsync(file, MaxAttachmentBytes, ct);
        var (type, ext) = FileSignatures.DetectChatFile(bytes, file.FileName)
            ?? throw new AppException("This file type can't be sent. Allowed: images, PDF, Word, Excel, PowerPoint, ZIP, TXT and CSV.");
        var attachment = new ChatAttachment
        {
            FileName = FileSignatures.SafeFileName(file.FileName, ext, "file"), ContentType = type, SizeBytes = bytes.Length, Data = bytes,
        };
        return await SendAsync(me, conversationId, caption ?? "", attachment, ct);
    }

    public async Task<ChatAttachment> GetAttachmentAsync(int me, long messageId, CancellationToken ct)
    {
        var msg = await db.ChatMessages.AsNoTracking().Where(m => m.MessageId == messageId)
                      .Select(m => new { m.ConversationId, m.IsDeleted }).FirstOrDefaultAsync(ct)
                  ?? throw new NotFoundException("File not found.");
        await EnsureMemberAsync(me, msg.ConversationId, ct);
        return (msg.IsDeleted ? null : await db.ChatAttachments.AsNoTracking().FirstOrDefaultAsync(a => a.MessageId == messageId, ct))
               ?? throw new NotFoundException("File not found.");
    }

    private async Task<ChatMessageDto> SendAsync(int me, int conversationId, string body, ChatAttachment? attachment, CancellationToken ct)
    {
        var text = body.Trim();
        if (text.Length == 0 && attachment is null) throw new AppException("Message cannot be empty.");
        var member = await db.ChatMembers.FirstOrDefaultAsync(m => m.ConversationId == conversationId && m.UserId == me, ct)
                     ?? throw new ForbiddenException("You are not part of this conversation.");
        var msg = new ChatMessage { ConversationId = conversationId, SenderUserId = me, Body = text, SentAt = UtcNow, Attachment = attachment };
        db.ChatMessages.Add(msg);
        await db.SaveChangesAsync(ct);

        member.LastReadMessageId = msg.MessageId;   // your own message counts as read
        await db.ChatConversations.Where(c => c.ConversationId == conversationId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.LastMessageAt, msg.SentAt), ct);
        await db.SaveChangesAsync(ct);

        var dto = await Project(db.ChatMessages.AsNoTracking().Where(m => m.MessageId == msg.MessageId)).FirstAsync(ct);
        await hub.Clients.Groups(await MemberGroupsAsync(conversationId, ct)).SendAsync("message", dto, ct);
        return dto;
    }

    public async Task MarkReadAsync(int me, int conversationId, long lastMessageId, CancellationToken ct)
    {
        var member = await db.ChatMembers.FirstOrDefaultAsync(m => m.ConversationId == conversationId && m.UserId == me, ct)
                     ?? throw new ForbiddenException("You are not part of this conversation.");
        var maxId = await db.ChatMessages.Where(m => m.ConversationId == conversationId).MaxAsync(m => (long?)m.MessageId, ct) ?? 0;
        var newValue = Math.Min(lastMessageId, maxId);
        if (newValue <= member.LastReadMessageId) return;
        member.LastReadMessageId = newValue;
        await db.SaveChangesAsync(ct);
        await hub.Clients.Groups(await MemberGroupsAsync(conversationId, ct))
            .SendAsync("read", new ReadReceiptDto(conversationId, me, newValue), ct);
    }

    public async Task DeleteMessageAsync(int me, long messageId, CancellationToken ct)
    {
        var msg = await db.ChatMessages.FirstOrDefaultAsync(m => m.MessageId == messageId, ct) ?? throw new NotFoundException("Message not found.");
        if (msg.SenderUserId != me) throw new ForbiddenException("You can only delete your own messages.");
        if (msg.IsDeleted) return;
        msg.IsDeleted = true;
        msg.Body = "";
        await db.SaveChangesAsync(ct);
        await db.ChatAttachments.Where(a => a.MessageId == messageId).ExecuteDeleteAsync(ct);   // the file goes too
        await hub.Clients.Groups(await MemberGroupsAsync(msg.ConversationId, ct)).SendAsync("messageDeleted", msg.ConversationId, messageId, ct);
    }

    // ------------------------------------------------------------------ Helpers
    private async Task<ChatConversationDto> GetOneAsync(int conversationId, int me, CancellationToken ct) =>
        (await BuildAsync([conversationId], me, ct)).Single();

    /// <summary>Sends each member their own view of the conversation (titles and unread counts differ per member).</summary>
    private async Task PushConversationAsync(int conversationId, CancellationToken ct)
    {
        var members = await db.ChatMembers.Where(m => m.ConversationId == conversationId).Select(m => m.UserId).ToListAsync(ct);
        foreach (var userId in members)
        {
            var view = (await BuildAsync([conversationId], userId, ct)).Single();
            await hub.Clients.Group(ChatHub.UserGroup(userId)).SendAsync("conversation", view, ct);
        }
    }

    private async Task<List<ChatConversationDto>> BuildAsync(List<int> ids, int me, CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        var convs = await db.ChatConversations.AsNoTracking().Where(c => ids.Contains(c.ConversationId))
            .Select(c => new { c.ConversationId, c.IsGroup, c.Name, c.LastMessageAt }).ToListAsync(ct);
        var members = await db.ChatMembers.AsNoTracking().Where(m => ids.Contains(m.ConversationId))
            .Select(m => new
            {
                m.ConversationId, m.UserId, m.IsAdmin, m.LastReadMessageId, m.User.LastSeenAt,
                Name = m.User.Employee != null ? m.User.Employee.FirstName + " " + m.User.Employee.LastName : m.User.Username,
                Designation = m.User.Employee != null ? m.User.Employee.Designation : null,
            })
            .ToListAsync(ct);
        var lastIds = await db.ChatMessages.Where(m => ids.Contains(m.ConversationId))
            .GroupBy(m => m.ConversationId).Select(g => g.Max(m => m.MessageId)).ToListAsync(ct);
        var last = await Project(db.ChatMessages.AsNoTracking().Where(m => lastIds.Contains(m.MessageId))).ToDictionaryAsync(m => m.ConversationId, ct);
        var myRead = members.Where(m => m.UserId == me).ToDictionary(m => m.ConversationId, m => m.LastReadMessageId);
        var unread = await db.ChatMessages.Where(m => ids.Contains(m.ConversationId) && m.SenderUserId != me && !m.IsDeleted)
            .Join(db.ChatMembers.Where(cm => cm.UserId == me), m => m.ConversationId, cm => cm.ConversationId, (m, cm) => new { m.ConversationId, m.MessageId, cm.LastReadMessageId })
            .Where(x => x.MessageId > x.LastReadMessageId)
            .GroupBy(x => x.ConversationId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        return convs.Select(c =>
        {
            var mem = members.Where(m => m.ConversationId == c.ConversationId)
                .Select(m => new ChatMemberDto(m.UserId, m.Name, m.Designation, m.IsAdmin, presence.IsOnline(m.UserId), m.LastSeenAt, m.LastReadMessageId))
                .OrderByDescending(m => m.Online).ThenBy(m => m.Name).ToList();
            var other = c.IsGroup ? null : mem.FirstOrDefault(m => m.UserId != me);
            return new ChatConversationDto(c.ConversationId, c.IsGroup, c.IsGroup ? c.Name ?? "Group" : other?.Name ?? "Chat", other?.UserId, mem,
                last.GetValueOrDefault(c.ConversationId), unread.GetValueOrDefault(c.ConversationId), c.LastMessageAt,
                mem.Any(m => m.UserId == me && m.IsAdmin));
        }).OrderByDescending(c => c.LastMessageAt).ToList();
    }

    private async Task<List<string>> MemberGroupsAsync(int conversationId, CancellationToken ct) =>
        (await db.ChatMembers.Where(m => m.ConversationId == conversationId).Select(m => m.UserId).ToListAsync(ct)).Select(ChatHub.UserGroup).ToList();

    private async Task EnsureMemberAsync(int me, int conversationId, CancellationToken ct)
    {
        if (!await db.ChatMembers.AnyAsync(m => m.ConversationId == conversationId && m.UserId == me, ct))
            throw new ForbiddenException("You are not part of this conversation.");
    }

    private async Task EnsureActiveUsersAsync(List<int> ids, CancellationToken ct)
    {
        var found = await db.Users.CountAsync(u => ids.Contains(u.UserId) && u.IsActive, ct);
        if (found != ids.Count) throw new AppException("Some of the selected people are not active users.");
    }

    private async Task<ChatConversation> LoadGroupAsAdminAsync(int me, int conversationId, CancellationToken ct)
    {
        var conv = await db.ChatConversations.Include(c => c.Members).FirstOrDefaultAsync(c => c.ConversationId == conversationId, ct)
                   ?? throw new NotFoundException("Conversation not found.");
        if (!conv.IsGroup) throw new AppException("This is not a group chat.");
        if (conv.Members.FirstOrDefault(m => m.UserId == me) is not { IsAdmin: true }) throw new ForbiddenException("Only group admins can do this.");
        return conv;
    }

    private static IQueryable<ChatMessageDto> Project(IQueryable<ChatMessage> q) => q.Select(m => new ChatMessageDto(
        m.MessageId, m.ConversationId, m.SenderUserId,
        m.Sender.Employee != null ? m.Sender.Employee.FirstName + " " + m.Sender.Employee.LastName : m.Sender.Username,
        m.IsDeleted ? "" : m.Body, m.SentAt, m.IsDeleted,
        m.IsDeleted || m.Attachment == null ? null : new ChatAttachmentDto(m.Attachment.FileName, m.Attachment.ContentType, m.Attachment.SizeBytes)));
}
