using System.Security.Claims;
using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Services;

public class AuthService
{
    private readonly AppDbContext _db;
    private readonly PasswordHasher<User> _hasher = new();

    public AuthService(AppDbContext db) => _db = db;

    public string HashPassword(string password) => _hasher.HashPassword(new User(), password);

    public bool VerifyPassword(User user, string password)
    {
        var result = _hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        return result is PasswordVerificationResult.Success
            or PasswordVerificationResult.SuccessRehashNeeded;
    }

    /// <summary>Validates a login by e-mail (primary) or, as a fallback, the username — so the
    /// seeded "admin" keeps working before an e-mail has been set.</summary>
    public async Task<User?> ValidateAsync(string identifier, string password)
    {
        var id = (identifier ?? "").Trim().ToLower();
        if (id.Length == 0) return null;
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email != null && u.Email.ToLower() == id)
                   ?? await _db.Users.FirstOrDefaultAsync(u => u.Username.ToLower() == id);
        if (user is null) return null;
        return VerifyPassword(user, password) ? user : null;
    }

    public async Task SignInAsync(HttpContext http, User user, bool persistent)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.Role, user.Role),
        };
        if (!string.IsNullOrWhiteSpace(user.DisplayName))
            claims.Add(new Claim("DisplayName", user.DisplayName));

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var props = new AuthenticationProperties { IsPersistent = persistent };

        await http.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            props);
    }

    public Task SignOutAsync(HttpContext http) =>
        http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
}
