using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

// A public profile is the only thing here a stranger may read; the rest is about the caller.
[ApiController]
[Route("api/users")]
public sealed class UsersController(AuthService auth) : ControllerBase {
    [AllowAnonymous]
    [HttpGet("{id:guid}")]
    public Task<UserBrief> ById(Guid id, CancellationToken token) =>
        auth.BriefAsync(id, token);

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
