using MatCMS.Shared;

namespace MatCMS.Services.Plugins;

/// <summary>
/// Whether a native plugin is the MatCMS Store's own, signed code (decision of 2026-11-11, <c>docs/builtin-extraction.md</c>): such a plugin
/// runs without "Native Plugins erlauben", may adopt a core table and may take the key of a plugin that used to ship with the CMS.
/// <para>The answer is never stored: it is computed from the files on disk and the signature the package brought, at every load. A
/// signature copied onto other files, a swapped side DLL or a changed version all make it "not signed" — the plugin then needs the switch
/// like any other native plugin.</para>
/// </summary>
public static class NativeTrust
{
    /// <summary>Further public keys (key id → base64 SubjectPublicKeyInfo) trusted IN ADDITION to <see cref="StoreKeys.Known"/> — tests only.</summary>
    public static IReadOnlyDictionary<string, string>? ExtraKeys { get; set; }

    private static IReadOnlyDictionary<string, string> Keys =>
        ExtraKeys is null ? StoreKeys.Known : StoreKeys.Known.Concat(ExtraKeys).GroupBy(k => k.Key).ToDictionary(g => g.Key, g => g.First().Value);

    /// <summary>True when the files in <paramref name="srcDir"/> carry a valid code signature for this key and version.</summary>
    public static bool IsSigned(string key, string version, string srcDir, string? signature) =>
        StoreSignature.VerifyCodeDir(key, version, srcDir, signature, Keys);

    /// <summary>True when this row is a native plugin whose installed files carry a valid code signature.</summary>
    public static bool IsSigned(IWebHostEnvironment env, Models.Plugin p) =>
        Models.PluginKind.Normalize(p.Kind) == Models.PluginKind.Native && !string.IsNullOrWhiteSpace(p.CodeSignature)
        && IsSigned(p.Key, p.Version, StoragePaths.PluginSrc(env, p.Key, p.Version), p.CodeSignature);
}
