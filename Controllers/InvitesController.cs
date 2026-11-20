using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

// Inviting a friend into a live room. Accepting settles the invite only; joining stays in the duels API.
[ApiController]
[Route("api/invites")]
[Authorize]
public sealed class InvitesController(InviteService invites) : ControllerBase {

    Guid Me => User.UserId() ?? throw AppError.Unauthorized("Нужен вход");

    [HttpGet]
    public async Task<List<InviteDto>> Inbox() => await invites.InboxAsync(Me);

    [HttpPost]
    public async Task<IActionResult> Send([FromBody] InviteCreate create) {
        var invite = await invites.SendAsync(Me, create.FriendId, create.RoomId);
        return StatusCode(StatusCodes.Status201Created, invite);
    }

    [HttpPost("{id:guid}/accept")]
    public async Task<InviteAccepted> Accept(Guid id) => await invites.AcceptAsync(Me, id);

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Refuse(Guid id) {
        await invites.RefuseAsync(Me, id);
        return NoContent();
    }
}
