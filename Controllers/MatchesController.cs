using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

// The history, the stats and the leaderboard. Reading is public: a guest who played a guest match
// can still see how it went, and a note stays with the two accounts that played it.
[ApiController]
[Route("api/matches")]
public sealed class MatchesController(MatchService matches) : ControllerBase {

    [HttpGet("")]
    [AllowAnonymous]
    public async Task<List<MatchDto>> Recent(int? take, CancellationToken cancellationToken) =>
        await matches.RecentAsync(take ?? MatchService.DefaultTake, cancellationToken);

    [HttpGet("{id:guid}")]
    [AllowAnonymous]
    public async Task<MatchDetailDto> Show(Guid id, CancellationToken cancellationToken) =>
        await matches.GetAsync(id, cancellationToken);

    [HttpPatch("{id:guid}")]
    [Authorize]
    public async Task<MatchDto> Note(Guid id, NoteRequest request, CancellationToken cancellationToken) =>
        await matches.SetNoteAsync(Me, id, request.Note, cancellationToken);

    [HttpDelete("{id:guid}")]
    [Authorize]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken) {
        await matches.DeleteAsync(Me, id, cancellationToken);
        return NoContent();
    }

    [HttpGet("/api/stats")]
    [AllowAnonymous]
    public async Task<StatsDto> Stats(CancellationToken cancellationToken) =>
        await matches.StatsAsync(cancellationToken);

    [HttpGet("/api/leaderboard")]
    [AllowAnonymous]
    public async Task<List<LeaderDto>> Leaders(int? take, CancellationToken cancellationToken) =>
        await matches.LeaderboardAsync(take ?? MatchService.DefaultTake, cancellationToken);

    Guid Me => HttpContext.User.UserId() ?? throw AppError.Unauthorized("Нужен вход");
}
