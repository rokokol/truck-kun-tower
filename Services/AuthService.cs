// Registration, sign-in and profile changes live together so the rules for a nickname and a
// password cannot drift apart between the endpoint that sets them and the one that checks them.
public sealed class AuthService(IUnitOfWork unit, TokenService tokens) {
    public const int MinPasswordLength = 8;
    public const int MaxPasswordLength = 72;

    public async Task<AuthResponse> RegisterAsync(RegisterRequest request, CancellationToken token = default) {
        var nickname = RequireValidNickname(request.Nickname);
        var password = RequireValidPassword(request.Password);
        if (await unit.Users.NicknameTakenAsync(nickname, token)) throw AppError.Conflict("Такой ник уже занят");

        var user = new User {
            Id = Guid.NewGuid(),
            Nickname = nickname,
            NicknameKey = Nicknames.Normalize(nickname),
            PasswordHash = PasswordHasher.Hash(password),
            CreatedAt = DateTime.UtcNow
        };
        unit.Users.Add(user);
        await unit.SaveAsync(token);
        return Session(user);
    }

    public async Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken token = default) {
        // The same answer for an unknown nickname and a wrong password, so the endpoint does not
        // tell a stranger which half of the pair they guessed
        var user = string.IsNullOrWhiteSpace(request.Nickname)
            ? null
            : await unit.Users.ByNicknameAsync(request.Nickname, token);
        if (user is null || string.IsNullOrEmpty(request.Password) ||
            !PasswordHasher.Verify(request.Password, user.PasswordHash))
            throw AppError.Unauthorized("Ник или пароль не подходят");
        return Session(user);
    }

    public async Task<UserDto> MeAsync(Guid userId, CancellationToken token = default) =>
        ToDto(await RequireAsync(userId, token));

    public async Task<UserBrief> BriefAsync(Guid userId, CancellationToken token = default) {
        var user = await unit.Users.ByIdAsync(userId, token) ?? throw AppError.NotFound("Игрок не найден");
        return ToBrief(user);
    }

    public async Task<UserDto> UpdateAsync(Guid userId, UpdateProfileRequest request, CancellationToken token = default) {
        var user = await RequireAsync(userId, token);
        var wantsNickname = !string.IsNullOrWhiteSpace(request.Nickname);
        var wantsPassword = !string.IsNullOrWhiteSpace(request.NewPassword);
        if (!wantsNickname && !wantsPassword)
            throw AppError.BadRequest("Нечего менять: укажите новый ник или новый пароль");

        var nickname = wantsNickname ? RequireValidNickname(request.Nickname) : user.Nickname;
        if (wantsPassword) {
            // The current password is checked before the nickname is looked at, so a caller who
            // cannot prove themselves learns nothing about who else holds a nickname
            if (!PasswordHasher.Verify(request.CurrentPassword ?? "", user.PasswordHash))
                throw AppError.Unauthorized("Текущий пароль не подходит");
            RequireValidPassword(request.NewPassword);
        }

        var key = Nicknames.Normalize(nickname);
        if (key != user.NicknameKey && await unit.Users.NicknameTakenAsync(nickname, token))
            throw AppError.Conflict("Такой ник уже занят");

        user.Nickname = nickname;
        user.NicknameKey = key;
        if (wantsPassword) user.PasswordHash = PasswordHasher.Hash(request.NewPassword!);
        await unit.SaveAsync(token);
        return ToDto(user);
    }

    public async Task DeleteAsync(Guid userId, DeleteAccountRequest request, CancellationToken token = default) {
        var user = await RequireAsync(userId, token);
        if (!PasswordHasher.Verify(request.Password ?? "", user.PasswordHash))
            throw AppError.Unauthorized("Пароль не подходит");
        // Friendships, invitations and the user's own side of a match go with it: the cascades are ours
        unit.Users.Remove(user);
        await unit.SaveAsync(token);
    }

    AuthResponse Session(User user) =>
        new(tokens.Issue(user), DateTime.UtcNow + TokenService.Lifetime, ToDto(user));

    async Task<User> RequireAsync(Guid userId, CancellationToken token) =>
        await unit.Users.ByIdAsync(userId, token)
        ?? throw AppError.Unauthorized("Сессия больше не действует, войдите заново");

    static string RequireValidNickname(string? nickname) {
        var error = Nicknames.Validate(nickname);
        if (error is not null) throw AppError.BadRequest(error);
        return nickname!.Trim();
    }

    // The ceiling is a sanity bound, not a strength rule: PBKDF2 takes any length, so this only
    // stops a pasted page from becoming somebody's password
    static string RequireValidPassword(string? password) {
        if (password is null || password.Length is < MinPasswordLength or > MaxPasswordLength)
            throw AppError.BadRequest($"Пароль — от {MinPasswordLength} до {MaxPasswordLength} символов");
        return password;
    }

    static UserDto ToDto(User user) => new(user.Id, user.Nickname, user.CreatedAt);
    static UserBrief ToBrief(User user) => new(user.Id, user.Nickname);
}
