using HrPayroll.BuildingBlocks.Hosting;
using HrPayroll.Chat.Hubs;

// Chat service: one-to-one and group chat with files, REST API + real-time delivery through the SignalR hub /hubs/chat
// (the gateway proxies WebSockets). Presence ("online" / last seen) is kept here.
var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults<ChatDbContext>(ChatDbContext.Layout, s => s.HubPath = "/hubs/chat");

builder.Services.AddSignalR();
builder.Services.AddSingleton<PresenceTracker>();
builder.Services.AddScoped<ChatService>();

var app = builder.Build();
app.UseServiceDefaults();
app.MapHub<ChatHub>("/hubs/chat");
app.Run();