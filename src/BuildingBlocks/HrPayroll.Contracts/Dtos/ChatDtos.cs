using System.ComponentModel.DataAnnotations;

namespace HrPayroll.Contracts.Dtos;

public record ChatContactDto(int UserId, string Name, string? Designation, string? Department, bool Online, DateTime? LastSeenAt);

public record ChatMemberDto(int UserId, string Name, string? Designation, bool IsAdmin, bool Online, DateTime? LastSeenAt, long LastReadMessageId);

public record ChatMessageDto(long MessageId, int ConversationId, int SenderUserId, string SenderName, string Body, DateTime SentAt, bool IsDeleted,
    ChatAttachmentDto? Attachment);

/// <summary>File metadata only - the bytes come from GET api/chat/attachments/{messageId}.</summary>
public record ChatAttachmentDto(string FileName, string ContentType, int SizeBytes);

/// <summary>A conversation as seen by the caller (title = other person's name for one-to-one chats).</summary>
public record ChatConversationDto(
    int ConversationId,
    bool IsGroup,
    string Title,
    int? OtherUserId,
    IReadOnlyList<ChatMemberDto> Members,
    ChatMessageDto? LastMessage,
    int Unread,
    DateTime LastMessageAt,
    bool IAmAdmin);

public record DirectChatRequest([Range(1, int.MaxValue)] int UserId);

public class CreateGroupRequest
{
    [Required, MaxLength(100)] public string Name { get; set; } = default!;
    [MinLength(1), MaxLength(200)] public List<int> MemberUserIds { get; set; } = [];
}

public record RenameGroupRequest([Required, MaxLength(100)] string Name);

public record AddMembersRequest([MinLength(1), MaxLength(200)] List<int> UserIds);

public record SendMessageRequest([Required, MaxLength(4000)] string Body);

public record MarkReadRequest([Range(0, long.MaxValue)] long LastMessageId);

// Real-time events pushed over SignalR
public record ReadReceiptDto(int ConversationId, int UserId, long LastReadMessageId);
public record PresenceDto(int UserId, bool Online, DateTime? LastSeenAt);
public record TypingDto(int ConversationId, int UserId, string Name);
