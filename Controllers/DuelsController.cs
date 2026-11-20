using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

// One duel room, from the first chair to the last wave. No [Authorize] on the class: a guest with
// an invite link plays with a seat token instead of an account, and this is the only way in.
[ApiController]
[Route("api/duels")]
public sealed class DuelsController(RoomService rooms) : ControllerBase {

    [HttpPost("")]
    [AllowAnonymous]
    public async Task<ActionResult<DuelSession>> Create(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] CreateDuelRequest? request,
        CancellationToken cancellationToken) {
        var side = Sides.Parse(request?.Side) ?? Sides.Defender;
        var session = await rooms.CreateAsync(HttpContext.Player(), side, cancellationToken);
        return Created($"/api/duels/{session.Id}", session);
    }

    [HttpGet("")]
    [Authorize]
    public async Task<List<RoomDto>> Mine(CancellationToken cancellationToken) =>
        await rooms.MineAsync(Me, cancellationToken);

    [HttpGet("{id}")]
    [AllowAnonymous]
    public async Task<RoomDto> Show(string id, CancellationToken cancellationToken) =>
        await rooms.RoomAsync(HttpContext.Player(), id, cancellationToken);

    [HttpPost("{id}/join")]
    [AllowAnonymous]
    public async Task<DuelSession> Join(string id,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] JoinDuelRequest? request,
        CancellationToken cancellationToken) =>
        await rooms.JoinAsync(HttpContext.Player(), id, Sides.Parse(request?.Side), cancellationToken);

    [HttpGet("{id}/state")]
    [AllowAnonymous]
    public async Task<object> State(string id, CancellationToken cancellationToken) =>
        await rooms.StateAsync(HttpContext.Player(), id, cancellationToken);

    [HttpPost("{id}/moves")]
    [AllowAnonymous]
    public async Task<object> Move(string id, Move move, CancellationToken cancellationToken) =>
        await rooms.MoveAsync(HttpContext.Player(), id, move, cancellationToken);

    [HttpPost("{id}/swap")]
    [AllowAnonymous]
    public async Task<object> Swap(string id, CancellationToken cancellationToken) =>
        await rooms.AskSwapAsync(HttpContext.Player(), id, cancellationToken);

    [HttpPost("{id}/swap/accept")]
    [AllowAnonymous]
    public async Task<object> Accept(string id, CancellationToken cancellationToken) =>
        await rooms.AnswerSwapAsync(HttpContext.Player(), id, accept: true, cancellationToken);

    [HttpPost("{id}/swap/decline")]
    [AllowAnonymous]
    public async Task<object> Decline(string id, CancellationToken cancellationToken) =>
        await rooms.AnswerSwapAsync(HttpContext.Player(), id, accept: false, cancellationToken);

    [HttpDelete("{id}")]
    [AllowAnonymous]
    public async Task<IActionResult> Cancel(string id, CancellationToken cancellationToken) {
        await rooms.CancelAsync(HttpContext.Player(), id, cancellationToken);
        return NoContent();
    }

    Guid Me => HttpContext.User.UserId() ?? throw AppError.Unauthorized("Нужен вход");
}
