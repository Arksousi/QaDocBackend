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

    /// <summary>Present, and "1", on a guest token. A guest has no row in Users.</summary>
    public const string Guest = "gst";
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

    /// <summary>
    /// A token for the "Continue as a guest" tour. It names no account, so it cannot be revoked
    /// per person and carries no role; what it unlocks is decided entirely by the guest claim:
    /// read-only, and only the demo projects.
    /// </summary>
    public LoginResponse CreateGuestToken()
    {
        var expires = DateTime.UtcNow.AddHours(settings.ExpiryHours);
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, GuestUser.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.UniqueName, GuestUser.Username),
            new Claim(QaDocClaims.Guest, "1"),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))
        };

        var token = new JwtSecurityToken(
            issuer: settings.Issuer,
            audience: settings.Audience,
            claims: claims,
            expires: expires,
            signingCredentials: new SigningCredentials(settings.SigningKey(), SecurityAlgorithms.HmacSha256));

        return new LoginResponse(new JwtSecurityTokenHandler().WriteToken(token), expires, GuestUser.Public());
    }
}

/// <summary>The person behind a guest token. Invented on the spot; nothing is stored.</summary>
public static class GuestUser
{
    /// <summary>No real row can have it: Users.UserId is an identity starting at 1.</summary>
    public const int Id = 0;
    public const string Username = "guest";
    public const string DisplayName = "Guest";

    public static User Public() => new()
    {
        UserId = Id, Username = Username, DisplayName = DisplayName,
        Role = Roles.Tester, IsActive = true, IsGuest = true, CreatedAt = DateTime.UtcNow
    };
}

public static class ClaimsPrincipalExtensions
{
    /// <summary>A guest tour rather than a signed-in account. Guests may only read, and only the demo.</summary>
    public static bool IsGuest(this ClaimsPrincipal principal) =>
        principal.FindFirstValue(QaDocClaims.Guest) == "1";

    /// <summary>Who is asking, for the queries that filter a list by what the caller may see.</summary>
    public static Viewer AsViewer(this ClaimsPrincipal principal) =>
        principal.IsGuest()
            ? new Viewer(GuestUser.Id, IsAdmin: false, IsGuest: true)
            : new Viewer(principal.GetUserId(), principal.IsInRole(Roles.Admin), IsGuest: false);

    /// <summary>Id of the signed-in user. Only valid inside [Authorize] endpoints.</summary>
    public static int GetUserId(this ClaimsPrincipal principal) =>
        int.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)
                  ?? principal.FindFirstValue(JwtRegisteredClaimNames.Sub)
                  ?? throw new InvalidOperationException("No signed-in user."));
}
