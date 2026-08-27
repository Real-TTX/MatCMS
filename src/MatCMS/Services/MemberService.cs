using System.Security.Claims;
using MatCMS.Data;
using MatCMS.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Services;

/// <summary>
/// The PUBLIC-site login (the "guest area"), deliberately a world apart from the admin
/// <see cref="AuthService"/>: its own cookie scheme (<see cref="Scheme"/>), its own table
/// (<see cref="SiteMember"/>), its own claims. A visitor can never become an administrator by
/// logging in here, and an admin session never counts as a member session.
/// <para>Passwords use the same <see cref="PasswordHasher{TUser}"/> the admin uses — one hashing
/// story for the whole app.</para>
/// </summary>
public class MemberService
{
    /// <summary>The authentication scheme (and cookie) name for site members. NOT the admin scheme.</summary>
    public const string Scheme = "Member";

    private readonly AppDbContext _db;
    private readonly PasswordHasher<SiteMember> _hasher = new();

    public MemberService(AppDbContext db) => _db = db;

    public string HashPassword(string password) => _hasher.HashPassword(new SiteMember(), password);

    public Task<SiteMember?> FindAsync(string username) =>
        _db.SiteMembers.FirstOrDefaultAsync(m => m.Username == username);

    /// <summary>Verifies credentials and returns the member, or null. Only active accounts pass.</summary>
    public async Task<SiteMember?> ValidateAsync(string username, string password)
    {
        var name = (username ?? "").Trim();
        if (name.Length == 0 || string.IsNullOrEmpty(password)) return null;

        var member = await _db.SiteMembers.FirstOrDefaultAsync(m => m.Username == name);
        if (member is null || !member.IsActive || string.IsNullOrEmpty(member.PasswordHash)) return null;

        var result = _hasher.VerifyHashedPassword(member, member.PasswordHash, password);
        return result == PasswordVerificationResult.Failed ? null : member;
    }

    /// <summary>Signs a member in under the member scheme. Roles ride as role claims, so a page's
    /// <see cref="Page.RequiredRole"/> can be checked with the ordinary <c>IsInRole</c>.</summary>
    public async Task SignInAsync(HttpContext http, SiteMember member, bool persistent)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, member.Id.ToString()),
            new(ClaimTypes.Name, member.Username),
            new("DisplayName", string.IsNullOrWhiteSpace(member.DisplayName) ? member.Username : member.DisplayName),
        };
        foreach (var role in member.Roles) claims.Add(new Claim(ClaimTypes.Role, role));

        var identity = new ClaimsIdentity(claims, Scheme);
        await http.SignInAsync(Scheme, new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = persistent });
    }

    public Task SignOutAsync(HttpContext http) => http.SignOutAsync(Scheme);

    /// <summary>The member behind the current request, or null. Authenticates the member scheme
    /// explicitly — the framework only auto-populates <c>HttpContext.User</c> for the DEFAULT (admin)
    /// scheme, so a members-only check must ask for this one by name.</summary>
    public static async Task<ClaimsPrincipal?> CurrentAsync(HttpContext http)
    {
        if (http.Items.TryGetValue(ItemsKey, out var cached)) return cached as ClaimsPrincipal;
        var result = await http.AuthenticateAsync(Scheme);
        var principal = result.Succeeded ? result.Principal : null;
        http.Items[ItemsKey] = principal;
        return principal;
    }

    private const string ItemsKey = "__member_principal";

    /// <summary>True when the current request has a member who satisfies the page's requirement:
    /// logged in, and — if the page names a role — holding it. An administrator always passes, the
    /// same courtesy the renderer already extends to unpublished pages.</summary>
    public static async Task<bool> CanViewAsync(HttpContext http, Page page)
    {
        if (page.Access != PageAccess.Members) return true;
        if (http.User.IsInRole("Admin")) return true;

        var member = await CurrentAsync(http);
        if (member?.Identity?.IsAuthenticated != true) return false;
        return string.IsNullOrWhiteSpace(page.RequiredRole) || member.IsInRole(page.RequiredRole!);
    }
}
