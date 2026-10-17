using System.Collections.Concurrent;
using HrPayroll.BuildingBlocks.Auth;
using HrPayroll.Chat.Data;
using HrPayroll.Contracts.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace HrPayroll.Chat.Hubs;

/// <summary>Who is online right now (connection count per user - a user may have several tabs / devices).</summary>
public class PresenceTracker
{
    private readonly ConcurrentDictionary<int, int> _connections = new();

    /// <summary>Returns true when the user just came online (first connection).</summary>
    public bool Connect(int userId) => _connections.AddOrUpdate(userId, 1, (_, n) => n + 1) == 1;

    /// <summary>Returns true when the user just went offline (last connection closed).</summary>
    public bool Disconnect(int userId)
    {
        while (true)
        {
            if (!_connections.TryGetValue(userId, out var n)) return false;
            if (n <= 1)
            {
                if (_connections.TryRemove(new KeyValuePair<int, int>(userId, n))) return true;
            }
            else if (_connections.TryUpdate(userId, n - 1, n)) return false;
        }
    }

    public bool IsOnline(int userId) => _connections.ContainsKey(userId);

    public IReadOnlyCollection<int> OnlineUserIds => _connections.Keys.ToList();
}

/// <summary>
/// Real-time chat channel. Messages are sent through the REST API (validated and stored) and pushed to
/// members through this hub; the hub itself only tracks presence and relays "typing…" signals.
/// Every connection joins a per-user group, so pushes reach all of a user's tabs and devices.
/// </summary>
[Authorize]
public class ChatHub(PresenceTracker presence, ChatDbContext db, TimeProvider time) : Hub
{
    public static string UserGroup(int userId) => $"user:{userId}";

    public override async Task OnConnectedAsync()
    {
        var userId = Context.User!.GetUserId();
        await Groups.AddToGroupAsync(Context.ConnectionId, UserGroup(userId));
        if (presence.Connect(userId))
            await Clients.Others.SendAsync("presence", new PresenceDto(userId, true, null));
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var userId = Context.User!.GetUserId();
        if (presence.Disconnect(userId))
        {
            var now = time.GetUtcNow().UtcDateTime;
            await db.Users.Where(u => u.UserId == userId).ExecuteUpdateAsync(s => s.SetProperty(u => u.LastSeenAt, now));
            await Clients.Others.SendAsync("presence", new PresenceDto(userId, false, now));
        }
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>Tells the other members of a conversation that the caller is typing.</summary>
    public async Task Typing(int conversationId)
    {
        var userId = Context.User!.GetUserId();
        var members = await db.ChatMembers.Where(m => m.ConversationId == conversationId).Select(m => m.UserId).ToListAsync();
        if (!members.Contains(userId)) return;
        var name = await db.Users.Where(u => u.UserId == userId)
            .Select(u => u.Employee != null ? u.Employee.FirstName : u.Username).FirstAsync();
        var others = members.Where(m => m != userId).Select(UserGroup).ToList();
        if (others.Count > 0) await Clients.Groups(others).SendAsync("typing", new TypingDto(conversationId, userId, name));
    }
}
