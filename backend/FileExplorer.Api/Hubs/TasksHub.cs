using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace FileExplorer.Api.Hubs;

[Authorize]
public class TasksHub : Hub;
