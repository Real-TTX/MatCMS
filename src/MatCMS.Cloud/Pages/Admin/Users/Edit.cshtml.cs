using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using ModelUser = MatCMS.Cloud.Models.User;   // PageModel.User (ClaimsPrincipal) shadows the model type

namespace MatCMS.Cloud.Pages.Admin.Users;

/// <summary>Create + edit in one page: no id = new user (password required), id = edit (an empty
/// password field keeps the current one). A user is either an <b>Admin</b> (full cloud access) or an
/// <b>Operator</b> scoped to the instances ticked below.</summary>
public class EditModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly AuthService _auth;

    public EditModel(AppDbContext db, AuthService auth)
    {
        _db = db;
        _auth = auth;
    }

    public int? Id { get; private set; }
    [BindProperty] public string Username { get; set; } = "";
    [BindProperty] public string? Email { get; set; }
    [BindProperty] public string? DisplayName { get; set; }
    [BindProperty] public string? Password { get; set; }
    [BindProperty] public string Role { get; set; } = ModelUser.RoleAdmin;
    /// <summary>Instances an Operator is scoped to (ignored when Role = Admin).</summary>
    [BindProperty] public List<int> InstanceIds { get; set; } = new();

    public bool IsNew => Id is null;
    public string? Error { get; private set; }

    /// <summary>All instances, for the assignment checkboxes.</summary>
    public List<Instance> AllInstances { get; private set; } = new();

    private async Task LoadInstancesAsync() =>
        AllInstances = await _db.Instances.AsNoTracking().OrderBy(i => i.Name).ToListAsync();

    public async Task<IActionResult> OnGetAsync(int? id)
    {
        await LoadInstancesAsync();
        if (id is null) return Page();

        var user = await _db.Users.Include(u => u.Instances).FirstOrDefaultAsync(u => u.Id == id.Value);
        if (user is null) return RedirectToPage("Index");

        Id = user.Id;
        Username = user.Username;
        Email = user.Email;
        DisplayName = user.DisplayName;
        Role = user.Role;
        InstanceIds = user.Instances.Select(x => x.InstanceId).ToList();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int? id)
    {
        Id = id;
        await LoadInstancesAsync();

        var name = (Username ?? "").Trim();
        if (name.Length == 0) { Error = "Benutzername ist erforderlich."; return Page(); }

        var role = Role == ModelUser.RoleOperator ? ModelUser.RoleOperator : ModelUser.RoleAdmin;

        var taken = await _db.Users.AnyAsync(u => u.Username == name && (id == null || u.Id != id));
        if (taken) { Error = "Dieser Benutzername ist bereits vergeben."; return Page(); }

        // Only keep instance ids that actually exist (a stale/forged checkbox value is ignored).
        var validIds = role == ModelUser.RoleOperator
            ? AllInstances.Where(i => InstanceIds.Contains(i.Id)).Select(i => i.Id).Distinct().ToList()
            : new List<int>();

        if (id is null)
        {
            if (string.IsNullOrWhiteSpace(Password)) { Error = "Passwort ist erforderlich."; return Page(); }
            var created = new User
            {
                Username = name,
                Email = Email?.Trim(),
                DisplayName = DisplayName?.Trim(),
                Role = role,
                PasswordHash = _auth.HashPassword(Password!)
            };
            _db.Users.Add(created);
            await _db.SaveChangesAsync();               // assigns created.Id
            foreach (var iid in validIds)
                _db.UserInstances.Add(new UserInstance { UserId = created.Id, InstanceId = iid });
        }
        else
        {
            var user = await _db.Users.Include(u => u.Instances).FirstOrDefaultAsync(u => u.Id == id.Value);
            if (user is null) return RedirectToPage("Index");

            // Never demote the last remaining admin — that would lock everyone out of the admin areas.
            if (user.Role == ModelUser.RoleAdmin && role != ModelUser.RoleAdmin)
            {
                var admins = await _db.Users.CountAsync(u => u.Role == ModelUser.RoleAdmin);
                if (admins <= 1)
                {
                    Error = "Der letzte Administrator kann nicht zum Operator gemacht werden.";
                    Role = ModelUser.RoleAdmin;
                    return Page();
                }
            }

            user.Username = name;
            user.Email = Email?.Trim();
            user.DisplayName = DisplayName?.Trim();
            user.Role = role;
            if (!string.IsNullOrWhiteSpace(Password))
                user.PasswordHash = _auth.HashPassword(Password);

            // Replace the instance scope wholesale (simplest correct: remove all, add the chosen).
            _db.UserInstances.RemoveRange(user.Instances);
            foreach (var iid in validIds)
                _db.UserInstances.Add(new UserInstance { UserId = user.Id, InstanceId = iid });
        }

        await _db.SaveChangesAsync();
        TempData["Flash"] = "Benutzer gespeichert.";
        return RedirectToPage("Index");
    }
}
