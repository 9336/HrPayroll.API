namespace HrPayroll.Chat.Entities;

/// <summary>A one-to-one or group conversation.</summary>
public class ChatConversation
{
    public int ConversationId { get; set; }
    public bool IsGroup { get; set; }
    /// <summary>Group name (null for one-to-one chats).</summary>
    public string? Name { get; set; }
    /// <summary>"smallerUserId:largerUserId" for one-to-one chats, so each pair has exactly one conversation.</summary>
    public string? DirectKey { get; set; }
    public int CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime LastMessageAt { get; set; }

    public List<ChatMember> Members { get; set; } = [];

    public static string KeyFor(int a, int b) => a < b ? $"{a}:{b}" : $"{b}:{a}";
}

public class ChatMember
{
    public int ConversationId { get; set; }
    public int UserId { get; set; }
    public bool IsAdmin { get; set; }
    public long LastReadMessageId { get; set; }
    public DateTime JoinedAt { get; set; }

    public User User { get; set; } = default!;
}

public class ChatMessage
{
    public long MessageId { get; set; }
    public int ConversationId { get; set; }
    public int SenderUserId { get; set; }
    public string Body { get; set; } = default!;
    public DateTime SentAt { get; set; }
    public bool IsDeleted { get; set; }

    public User Sender { get; set; } = default!;
    public ChatAttachment? Attachment { get; set; }
}

/// <summary>A file sent with a chat message (at most one per message; the bytes are only loaded on download).</summary>
public class ChatAttachment
{
    public long MessageId { get; set; }
    public string FileName { get; set; } = default!;
    public string ContentType { get; set; } = default!;
    public int SizeBytes { get; set; }
    public byte[] Data { get; set; } = default!;
}
