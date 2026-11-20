using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

// Everything about a friend list. The services own the rules; these actions only name the caller.
[ApiController]
[Route("api/friends")]
[Authorize]
public sealed class FriendsController(FriendService friends) : ControllerBase {

    Guid Me => User.UserId() ?? throw AppError.Unauthorized("Нужен вход");

    [HttpGet]
    public async Task<List<FriendDto>> List() => await friends.ListAsync(Me);

    [HttpGet("requests")]
    public async Task<RequestListDto> Requests() => await friends.RequestsAsync(Me);

    // 201: a request is a new row, and its id is what the addressee answers with
    [HttpPost("requests")]
    public async Task<IActionResult> Ask([FromBody] FriendRequestCreate create) {
        var request = await friends.AskAsync(Me, create.Nickname);
        return StatusCode(StatusCodes.Status201Created, request);
    }

    [HttpPost("requests/{id:guid}/accept")]
    public async Task<IncomingRequestDto> Accept(Guid id) => await friends.AcceptAsync(Me, id);

    [HttpDelete("requests/{id:guid}")]
    public async Task<IActionResult> Refuse(Guid id) {
        await friends.RefuseAsync(Me, id);
        return NoContent();
    }

    [HttpDelete("{friendId:guid}")]
    public async Task<IActionResult> Remove(Guid friendId) {
        await friends.RemoveAsync(Me, friendId);
        return NoContent();
    }
}
