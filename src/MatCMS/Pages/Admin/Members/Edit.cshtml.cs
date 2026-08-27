using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Pages.Admin.Members;

/// <summary>Create (no id) or edit one site member. Password is required when creating and optional
/// when editing (left blank = keep the current one). Roles are ticked from the managed role list.</summary>
public class EditModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly MemberService _members;
    public EditModel(AppDbContext db, MemberService members) { _db = db; _members = members; }

    [BindProperty] public InputModel Input { get; set; } = new();
    public List<string> AllRoles { get; private set; } = new();
    public bool IsNew => Input.Id == 0;
    public string? Error { get; private set; }

    public class InputModel
    {
        public int Id { get; set; }
        public string Username { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string? Password { get; set; }
        public bool IsActive { get; set; } = true;
        public List<string> Roles { get; set; } = new();
    }

    public async Task<IActionResult> OnGetAsync(int? id)
    {
        AllRoles = await _db.SiteRoles.AsNoTracking().OrderBy(r => r.Name).Select(r => r.Name).ToListAsync();
        if (id is int mid)
        {
            var m = await _db.SiteMembers.FindAsync(mid);
            if (m is null) return RedirectToPage("Index");
            Input = new InputModel
            {
                Id = m.Id, Username = m.Username, DisplayName = m.DisplayName,
                IsActive = m.IsActive, Roles = m.Roles.ToList()
            };
        }
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        AllRoles = await _db.SiteRoles.AsNoTracking().OrderBy(r => r.Name).Select(r => r.Name).ToListAsync();

        var username = (Input.Username ?? "").Trim();
        if (username.Length == 0)
        {
            Error = "Benutzername ist erforderlich.";
            return Page();
        }
        if (await _db.SiteMembers.AnyAsync(m => m.Username == username && m.Id != Input.Id))
        {
            Error = $"Der Benutzername „{username}“ ist bereits vergeben.";
            return Page();
        }

        // Only roles that actually exist survive — a stale tick cannot invent a role.
        var roles = (Input.Roles ?? new()).Where(r => AllRoles.Contains(r)).Distinct();
        var rolesCsv = string.Join(",", roles);

        if (IsNew)
        {
            if (string.IsNullOrEmpty(Input.Password))
            {
                Error = "Ein Passwort ist beim Anlegen erforderlich.";
                return Page();
            }
            _db.SiteMembers.Add(new SiteMember
            {
                Username = username,
                DisplayName = (Input.DisplayName ?? "").Trim(),
                PasswordHash = _members.HashPassword(Input.Password!),
                RolesCsv = rolesCsv,
                IsActive = Input.IsActive
            });
        }
        else
        {
            var m = await _db.SiteMembers.FindAsync(Input.Id);
            if (m is null) return RedirectToPage("Index");
            m.Username = username;
            m.DisplayName = (Input.DisplayName ?? "").Trim();
            m.RolesCsv = rolesCsv;
            m.IsActive = Input.IsActive;
            // Blank password on edit = keep the existing one.
            if (!string.IsNullOrEmpty(Input.Password)) m.PasswordHash = _members.HashPassword(Input.Password!);
        }

        await _db.SaveChangesAsync();
        TempData["Flash"] = IsNew ? "Mitglied angelegt." : "Mitglied gespeichert.";
        return RedirectToPage("Index");
    }
}
