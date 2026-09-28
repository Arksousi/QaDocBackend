using Microsoft.AspNetCore.Mvc;
using QaDocBackend.Infrastructure;
using QaDocBackend.Models;
using QaDocBackend.Repositories;

namespace QaDocBackend.Controllers;

/// <summary>
/// The signed-in user's own notifications. There is no user id in any route: every call is about
/// the caller, so nobody can read or clear someone else's. A guest has none, and GuestReadOnlyFilter
/// already refuses their writes.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class NotificationsController(INotificationRepository notifications) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IEnumerable<Notification>>> Get([FromQuery] int top = 20) =>
        Ok(await notifications.GetAsync(User.AsViewer(), Math.Clamp(top, 1, 50)));

    /// <summary>Cheap enough for the app to poll every minute.</summary>
    [HttpGet("unread-count")]
    public async Task<ActionResult<UnreadCount>> GetUnreadCount() =>
        Ok(new UnreadCount(await notifications.CountUnreadAsync(User.AsViewer())));

    [HttpPost("{id:int}/read")]
    public async Task<ActionResult> MarkRead(int id) =>
        await notifications.MarkReadAsync(User.GetUserId(), id)
            ? NoContent()
            : NotFound(new { message = $"Notification #{id} not found." });

    [HttpPost("read-all")]
    public async Task<ActionResult> MarkAllRead()
    {
        await notifications.MarkAllReadAsync(User.GetUserId());
        return NoContent();
    }
}
