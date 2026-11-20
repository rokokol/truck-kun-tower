using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

// Auth is the only place a password is compared, so these tests exercise the service against
// mocked repositories: no database, no HTTP, just the rules that decide who may in.
public sealed class AuthTests {
    const string Password = "korrekt-horse";

    static AuthService Service(out Mock<IUserRepository> users) {
        users = new Mock<IUserRepository>();
        var unit = new Mock<IUnitOfWork>();
        unit.SetupGet(row => row.Users).Returns(users.Object);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:Key"] = new string('k', 48) })
            .Build();
        return new AuthService(unit.Object, new TokenService(configuration, NullLogger<TokenService>.Instance));
    }

    static User Existing(string nickname = "Ваня", string password = Password) => new() {
        Id = Guid.NewGuid(),
        Nickname = nickname,
        NicknameKey = Nicknames.Normalize(nickname),
        PasswordHash = PasswordHasher.Hash(password),
        CreatedAt = DateTime.UtcNow
    };

    [Fact]
    public async Task Register_stores_a_hash_and_the_normalized_nickname() {
        var auth = Service(out var users);
        User? added = null;
        users.Setup(row => row.NicknameTakenAsync("Кузьма", It.IsAny<CancellationToken>())).ReturnsAsync(false);
        users.Setup(row => row.Add(It.IsAny<User>())).Callback<User>(user => added = user);

        var response = await auth.RegisterAsync(new RegisterRequest("  Кузьма ", Password));

        Assert.NotNull(added);
        Assert.StartsWith("pbkdf2$", added!.PasswordHash);
        Assert.DoesNotContain(Password, added.PasswordHash);
        Assert.Equal("Кузьма", added.Nickname);
        Assert.Equal("кузьма", added.NicknameKey);
        Assert.True(PasswordHasher.Verify(Password, added.PasswordHash));
        Assert.NotEmpty(response.Token);
        Assert.Equal(added.Id, response.User.Id);
        users.Verify(row => row.Add(It.IsAny<User>()), Times.Once);
    }

    [Fact]
    public async Task Register_refuses_a_taken_nickname() {
        var auth = Service(out var users);
        users.Setup(row => row.NicknameTakenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var error = await Assert.ThrowsAsync<AppError>(() => auth.RegisterAsync(new RegisterRequest("Ваня", Password)));

        Assert.Equal(409, error.Status);
        users.Verify(row => row.Add(It.IsAny<User>()), Times.Never);
    }

    [Fact]
    public async Task Register_refuses_a_short_password() {
        var auth = Service(out var users);

        var error = await Assert.ThrowsAsync<AppError>(() => auth.RegisterAsync(new RegisterRequest("Ваня", "short")));

        Assert.Equal(400, error.Status);
        users.Verify(row => row.NicknameTakenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Login_with_a_wrong_password_is_unauthorized() {
        var auth = Service(out var users);
        users.Setup(row => row.ByNicknameAsync("Ваня", It.IsAny<CancellationToken>())).ReturnsAsync(Existing());

        var error = await Assert.ThrowsAsync<AppError>(() =>
            auth.LoginAsync(new LoginRequest("Ваня", "not-the-password")));

        Assert.Equal(401, error.Status);
    }

    [Fact]
    public async Task Login_with_an_unknown_nickname_says_the_same_thing() {
        var auth = Service(out var users);
        users.Setup(row => row.ByNicknameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((User?)null);

        var error = await Assert.ThrowsAsync<AppError>(() => auth.LoginAsync(new LoginRequest("Никто", Password)));

        Assert.Equal(401, error.Status);
    }

    [Fact]
    public async Task Login_with_the_right_password_issues_a_token() {
        var auth = Service(out var users);
        users.Setup(row => row.ByNicknameAsync("Ваня", It.IsAny<CancellationToken>())).ReturnsAsync(Existing());

        var response = await auth.LoginAsync(new LoginRequest("Ваня", Password));

        Assert.NotEmpty(response.Token);
        Assert.Equal("Ваня", response.User.Nickname);
        Assert.True(response.ExpiresAt > DateTime.UtcNow);
    }

    [Fact]
    public async Task Update_refuses_a_nickname_someone_else_holds() {
        var auth = Service(out var users);
        var user = Existing();
        users.Setup(row => row.ByIdAsync(user.Id, It.IsAny<CancellationToken>())).ReturnsAsync(user);
        users.Setup(row => row.NicknameTakenAsync("Петя", It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var error = await Assert.ThrowsAsync<AppError>(() =>
            auth.UpdateAsync(user.Id, new UpdateProfileRequest("Петя", null, null)));

        Assert.Equal(409, error.Status);
        Assert.Equal("Ваня", user.Nickname);
        users.Verify(row => row.ByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Update_refuses_a_password_change_without_the_current_password() {
        var auth = Service(out var users);
        var user = Existing();
        users.Setup(row => row.ByIdAsync(user.Id, It.IsAny<CancellationToken>())).ReturnsAsync(user);

        var error = await Assert.ThrowsAsync<AppError>(() =>
            auth.UpdateAsync(user.Id, new UpdateProfileRequest(null, "wrong-one", "another-horse")));

        Assert.Equal(401, error.Status);
        Assert.True(PasswordHasher.Verify(Password, user.PasswordHash));
    }

    [Fact]
    public async Task Update_needs_at_least_one_field() {
        var auth = Service(out var users);
        var user = Existing();
        users.Setup(row => row.ByIdAsync(user.Id, It.IsAny<CancellationToken>())).ReturnsAsync(user);

        var error = await Assert.ThrowsAsync<AppError>(() =>
            auth.UpdateAsync(user.Id, new UpdateProfileRequest(null, null, null)));

        Assert.Equal(400, error.Status);
    }

    [Fact]
    public async Task Update_changes_the_nickname_and_the_password_together() {
        var auth = Service(out var users);
        var user = Existing();
        users.Setup(row => row.ByIdAsync(user.Id, It.IsAny<CancellationToken>())).ReturnsAsync(user);
        users.Setup(row => row.NicknameTakenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var updated = await auth.UpdateAsync(user.Id, new UpdateProfileRequest("Петя", Password, "another-horse"));

        Assert.Equal("Петя", updated.Nickname);
        Assert.Equal("петя", user.NicknameKey);
        Assert.False(PasswordHasher.Verify(Password, user.PasswordHash));
        Assert.True(PasswordHasher.Verify("another-horse", user.PasswordHash));
    }

    [Fact]
    public async Task Delete_with_a_wrong_password_keeps_the_user() {
        var auth = Service(out var users);
        var user = Existing();
        users.Setup(row => row.ByIdAsync(user.Id, It.IsAny<CancellationToken>())).ReturnsAsync(user);

        var error = await Assert.ThrowsAsync<AppError>(() =>
            auth.DeleteAsync(user.Id, new DeleteAccountRequest("not-the-password")));

        Assert.Equal(401, error.Status);
        users.Verify(row => row.Remove(It.IsAny<User>()), Times.Never);
    }

    [Fact]
    public async Task Delete_with_the_right_password_removes_the_user() {
        var auth = Service(out var users);
        var user = Existing();
        users.Setup(row => row.ByIdAsync(user.Id, It.IsAny<CancellationToken>())).ReturnsAsync(user);

        await auth.DeleteAsync(user.Id, new DeleteAccountRequest(Password));

        users.Verify(row => row.Remove(user), Times.Once);
    }

    [Fact]
    public async Task Me_reports_a_missing_user_as_unauthorized() {
        var auth = Service(out var users);
        users.Setup(row => row.ByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((User?)null);

        var error = await Assert.ThrowsAsync<AppError>(() => auth.MeAsync(Guid.NewGuid()));

        Assert.Equal(401, error.Status);
    }

    [Fact]
    public async Task A_public_profile_of_a_stranger_is_not_found() {
        var auth = Service(out var users);
        users.Setup(row => row.ByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((User?)null);

        var error = await Assert.ThrowsAsync<AppError>(() => auth.BriefAsync(Guid.NewGuid()));

        Assert.Equal(404, error.Status);
    }
}
