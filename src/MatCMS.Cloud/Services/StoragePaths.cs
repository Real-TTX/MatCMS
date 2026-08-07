namespace MatCMS.Cloud.Services;

/// <summary>
/// Single source of truth for the runtime data locations. Everything lives under <c>appdata/</c>
/// (mounted as the Docker volume at /app/appdata), so one volume holds the whole app state.
/// NOTE: the folder is "appdata", not "data" — "data" would clash with the source <c>Data/</c>
/// folder in .dockerignore on case-insensitive (Windows) build hosts.
/// </summary>
public static class StoragePaths
{
    public static string Root(IWebHostEnvironment env) =>
        Path.Combine(env.ContentRootPath, "appdata");

    public static string Keys(IWebHostEnvironment env) => Path.Combine(Root(env), "keys");
}
