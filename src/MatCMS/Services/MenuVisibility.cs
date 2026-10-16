using MatCMS.Models;

namespace MatCMS.Services;

/// <summary>Which menu entries a visitor gets, by whether they are signed in as a member. Pure, so it is tested without a request.</summary>
public static class MenuVisibility
{
    /// <summary>Entries for members are dropped for a guest and the other way round; an entry whose parent is dropped goes with it (a hidden "Konto" must not leave its "Abmelden" hanging at the top level).</summary>
    public static List<MenuItem> Filter(IReadOnlyList<MenuItem> items, bool signedIn)
    {
        bool Shown(MenuItem i) => i.Visibility switch { MenuItem.ForMembers => signedIn, MenuItem.ForGuests => !signedIn, _ => true };
        var byId = items.ToDictionary(i => i.Id);
        bool Visible(MenuItem i)
        {
            for (var guard = 0; guard < 16; guard++)                       // menus are at most a few levels deep; the bound only protects against a damaged loop
            {
                if (!Shown(i)) return false;
                if (i.ParentId is not int pid || !byId.TryGetValue(pid, out var parent)) return true;
                i = parent;
            }
            return false;
        }
        return items.Where(Visible).ToList();
    }
}
