using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

// A public profile is the only thing here a stranger may read; the rest is about the caller.
[ApiController]
[Route("api/users")]
public sealed class UsersController(AuthService auth, Presence presence, IUnitOfWork unit) : ControllerBase {
    [AllowAnonymous]
    [HttpGet("{id:guid}")]
    public Task<UserBrief> ById(Guid id, CancellationToken token) =>
        auth.BriefAsync(id, token);

    [Authorize]
    [HttpGet("online")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<OnlineDto> Online([FromQuery] Guid[] ids, CancellationToken token, bool sample = true) {
        var me = UserId();
        var online = presence.Snapshot();
        var known = ids.Take(5).ToHashSet();
        var others = online.Where(id => id != me && (sample || known.Contains(id))).ToArray();
        if (sample) Random.Shared.Shuffle(others);
        var chosen = others.Take(5).ToArray();
        var users = await unit.Users.ByIdsAsync(chosen, token);
        var names = users.ToDictionary(user => user.Id, user => user.Nickname);
        return new(online.Length, chosen.Where(names.ContainsKey).Select(id => new UserBrief(id, names[id])).ToList());
    }

    [Authorize]
    [HttpPut("me")]
    public Task<UserDto> Update(UpdateProfileRequest request, CancellationToken token) =>
        auth.UpdateAsync(UserId(), request, token);

    [Authorize]
    [HttpDelete("me")]
    public async Task<IActionResult> Delete(DeleteAccountRequest request, CancellationToken token) {
        await auth.DeleteAsync(UserId(), request, token);
        return NoContent();
    }

    Guid UserId() => HttpContext.User.UserId()
        ?? throw AppError.Unauthorized("Сессия больше не действует, войдите заново");
}
