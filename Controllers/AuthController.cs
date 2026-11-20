using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

// Sign-in and sign-out: every rule lives in AuthService, this file only picks the status code.
[ApiController]
[Route("api/auth")]
public sealed class AuthController(AuthService auth) : ControllerBase {
    [AllowAnonymous]
    [HttpPost("register")]
    public Task<AuthResponse> Register(RegisterRequest request, CancellationToken token) =>
        auth.RegisterAsync(request, token);

    [AllowAnonymous]
    [HttpPost("login")]
    public Task<AuthResponse> Login(LoginRequest request, CancellationToken token) =>
        auth.LoginAsync(request, token);

    [Authorize]
    [HttpGet("me")]
    public Task<UserDto> Me(CancellationToken token) =>
        auth.MeAsync(UserId(), token);

    Guid UserId() => HttpContext.User.UserId()
        ?? throw AppError.Unauthorized("Сессия больше не действует, войдите заново");
}
