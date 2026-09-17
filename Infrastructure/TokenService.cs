using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using QaDocBackend.Models;

namespace QaDocBackend.Infrastructure;

public static class QaDocClaims
{
    /// <summary>Must match Users.TokenVersion; bumping the column revokes every token issued before.</summary>
    public const string TokenVersion = "ver";
}

public class JwtSettings
{
    public string Issuer { get; set; } = "QaDoc";
    public string Audience { get; set; } = "QaDoc";
    public int ExpiryHours { get; set; } = 8;
    public string Key { get; set; } = string.Empty;

    public SymmetricSecurityKey SigningKey()
    {
        if (Encoding.UTF8.GetByteCount(Key) < 32)
            throw new InvalidOperationException(
                "Jwt:Key is missing or shorter than 32 bytes. Set it in appsettings.Development.json or the Jwt__Key environment variable.");
        return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Key));
    }
}

public class TokenService(JwtSettings settings)
{
    public LoginResponse CreateToken(UserRecord user)
    {
        var expires = DateTime.UtcNow.AddHours(settings.ExpiryHours);
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.UserId.ToString()),
            new Claim(JwtRegisteredClaimNames.UniqueName, user.Username),
            new Claim(QaDocClaims.TokenVersion, user.TokenVersion.ToString()),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))
        };

        var token = new JwtSecurityToken(
            issuer: settings.Issuer,
            audience: settings.Audience,
            claims: claims,
            expires: expires,
            signingCredentials: new SigningCredentials(settings.SigningKey(), SecurityAlgorithms.HmacSha256));

        var publicUser = new User
        {
            UserId = user.UserId, Username = user.Username, DisplayName = user.DisplayName,
            Role = user.Role, IsActive = user.IsActive, CreatedAt = user.CreatedAt
        };
        return new LoginResponse(new JwtSecurityTokenHandler().WriteToken(token), expires, publicUser);
    }
}

public static class ClaimsPrincipalExtensions
{
    /// <summary>Id of the signed-in user. Only valid inside [Authorize] endpoints.</summary>
    public static int GetUserId(this ClaimsPrincipal principal) =>
        int.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)
                  ?? principal.FindFirstValue(JwtRegisteredClaimNames.Sub)
                  ?? throw new InvalidOperationException("No signed-in user."));
}
