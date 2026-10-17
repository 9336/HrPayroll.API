using HrPayroll.BuildingBlocks.Auth;
using HrPayroll.Chat.Hubs;
using HrPayroll.Contracts.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HrPayroll.Chat.Controllers;

/// <summary>Team chat (one-to-one and groups). Real-time delivery goes through the SignalR hub at /hubs/chat.</summary>
[ApiController]
[Route("api/chat")]
[Authorize]
public class ChatController(ChatService chat) : ControllerBase
{
    private int Me => User.GetUserId();

    [HttpGet("contacts")]
    public async Task<ActionResult<List<ChatContactDto>>> Contacts(CancellationToken ct) => Ok(await chat.GetContactsAsync(Me, ct));

    [HttpGet("online")]
    public ActionResult<IReadOnlyCollection<int>> Online() => Ok(chat.OnlineUserIds());

    [HttpGet("conversations")]
    public async Task<ActionResult<List<ChatConversationDto>>> Conversations(CancellationToken ct) => Ok(await chat.GetConversationsAsync(Me, ct));

    /// <summary>Opens (or creates) the one-to-one chat with a user.</summary>
    [HttpPost("conversations/direct")]
    public async Task<ActionResult<ChatConversationDto>> Direct(DirectChatRequest request, CancellationToken ct) =>
        Ok(await chat.GetOrCreateDirectAsync(Me, request.UserId, ct));

    [HttpPost("conversations/group")]
    public async Task<ActionResult<ChatConversationDto>> Group(CreateGroupRequest request, CancellationToken ct) =>
        Ok(await chat.CreateGroupAsync(Me, request, ct));

    [HttpPut("conversations/{id:int}")]
    public async Task<ActionResult<ChatConversationDto>> Rename(int id, RenameGroupRequest request, CancellationToken ct) =>
        Ok(await chat.RenameGroupAsync(Me, id, request.Name, ct));

    [HttpPost("conversations/{id:int}/members")]
    public async Task<ActionResult<ChatConversationDto>> AddMembers(int id, AddMembersRequest request, CancellationToken ct) =>
        Ok(await chat.AddMembersAsync(Me, id, request.UserIds, ct));

    /// <summary>Remove a member (group admin) or leave the group (your own user id).</summary>
    [HttpDelete("conversations/{id:int}/members/{userId:int}")]
    public async Task<IActionResult> RemoveMember(int id, int userId, CancellationToken ct)
    {
        await chat.RemoveMemberAsync(Me, id, userId, ct);
        return NoContent();
    }

    [HttpGet("conversations/{id:int}/messages")]
    public async Task<ActionResult<List<ChatMessageDto>>> Messages(int id, [FromQuery] long? before, [FromQuery] int take = 50, CancellationToken ct = default) =>
        Ok(await chat.GetMessagesAsync(Me, id, before, take, ct));

    [HttpPost("conversations/{id:int}/messages")]
    public async Task<ActionResult<ChatMessageDto>> Send(int id, SendMessageRequest request, CancellationToken ct) =>
        Ok(await chat.SendAsync(Me, id, request.Body, ct));

    /// <summary>Sends a file (multipart: file + optional caption).</summary>
    [HttpPost("conversations/{id:int}/attachments")]
    [RequestSizeLimit(ChatService.MaxAttachmentBytes + 64 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = ChatService.MaxAttachmentBytes + 64 * 1024)]
    public async Task<ActionResult<ChatMessageDto>> SendFile(int id, IFormFile file, [FromForm] string? caption, CancellationToken ct) =>
        Ok(await chat.SendFileAsync(Me, id, file, caption, ct));

    /// <summary>Downloads a message's file (conversation members only).</summary>
    [HttpGet("attachments/{messageId:long}")]
    public async Task<IActionResult> Attachment(long messageId, CancellationToken ct)
    {
        var file = await chat.GetAttachmentAsync(Me, messageId, ct);
        Response.Headers.XContentTypeOptions = "nosniff";
        return File(file.Data, file.ContentType, file.FileName);
    }

    [HttpPost("conversations/{id:int}/read")]
    public async Task<IActionResult> Read(int id, MarkReadRequest request, CancellationToken ct)
    {
        await chat.MarkReadAsync(Me, id, request.LastMessageId, ct);
        return NoContent();
    }

    [HttpDelete("messages/{messageId:long}")]
    public async Task<IActionResult> DeleteMessage(long messageId, CancellationToken ct)
    {
        await chat.DeleteMessageAsync(Me, messageId, ct);
        return NoContent();
    }
}
