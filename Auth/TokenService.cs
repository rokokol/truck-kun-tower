using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;

// One place that turns a user into a bearer token and back. A missing key is a random key with a
// loud warning: a hard-coded default would be the same secret on every deployment.
public sealed class TokenService {
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);
    public const string Issuer = "truck-kun-tower";
    public const string Audience = "truck-kun-tower-web";

    readonly SymmetricSecurityKey key;

    public TokenService(IConfiguration configuration, ILogger<TokenService> log) {
        key = new SymmetricSecurityKey(ResolveKey(configuration, log));
    }

    // The same key that signs also validates, so a random fallback stays consistent in one process
    public TokenValidationParameters Validation => new() {
        ValidateIssuer = true,
        ValidIssuer = Issuer,
        ValidateAudience = true,
        ValidAudience = Audience,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = key,
        ValidateLifetime = true,
        ClockSkew = TimeSpan.FromSeconds(30)
    };

    public string Issue(User user) {
        var claims = new[] {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Name, user.Nickname),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))
        };
        var token = new JwtSecurityToken(
            issuer: Issuer, audience: Audience, claims: claims, notBefore: DateTime.UtcNow,
            expires: DateTime.UtcNow.Add(Lifetime),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    static byte[] ResolveKey(IConfiguration configuration, ILogger log) {
        var configured = configuration["Jwt:Key"];
        if (!string.IsNullOrWhiteSpace(configured)) {
            var bytes = Encoding.UTF8.GetBytes(configured);
            if (bytes.Length < 32) throw new InvalidOperationException("Jwt__Key must be at least 32 bytes");
            return bytes;
        }
        log.LogWarning("Jwt:Key is not set, so tokens are signed with a random key and every restart signs players out");
        return RandomNumberGenerator.GetBytes(48);
    }
}
