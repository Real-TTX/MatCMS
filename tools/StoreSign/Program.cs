// StoreSign — signs packages for the MatCMS Store on the store operator's own machine (docs/official-store.md §6).
//
//   dotnet run --project tools/StoreSign -- keygen                          create the key pair (once; refuses to overwrite)
//   dotnet run --project tools/StoreSign -- pubkey                          show the public key (id, base64) of the existing key
//   dotnet run --project tools/StoreSign -- sign <kind> <key> <version> <sha256>   what the store's review page asks for → "keyid:signature"
//   dotnet run --project tools/StoreSign -- sign-package <bundle.zip>       reads key/version/kind from plugin.json, hashes the file, signs
//
// The private key lives in %APPDATA%\MatCMS\store-signing\key.pem (or the file MATCMS_STORE_KEY names). It never goes into the repository or onto a
// server: whoever takes over the store server must still not be able to sign. Back it up yourself — a lost key means a new key in StoreKeys.cs and
// therefore a new MatCMS version before anything can be signed again.
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using MatCMS.Shared;

var keyPath = Environment.GetEnvironmentVariable("MATCMS_STORE_KEY") is { Length: > 0 } p
    ? p
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MatCMS", "store-signing", "key.pem");

if (args.Length == 0) return Usage();
switch (args[0])
{
    case "keygen":
    {
        if (File.Exists(keyPath)) { Console.Error.WriteLine($"Es gibt schon einen Schlüssel: {keyPath} — nichts überschrieben."); return 1; }
        Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        File.WriteAllText(keyPath, ec.ExportPkcs8PrivateKeyPem());
        Console.WriteLine($"Schlüssel angelegt: {keyPath}  (sichern!)");
        PrintPublic(ec);
        return 0;
    }
    case "pubkey":
    {
        using var ec = Load();
        if (ec is null) return 1;
        PrintPublic(ec);
        return 0;
    }
    case "sign" when args.Length == 5:
    {
        using var ec = Load();
        if (ec is null) return 1;
        Console.WriteLine(Token(ec, StoreSignature.Text(args[1], args[2], args[3], args[4])));
        return 0;
    }
    case "sign-package" when args.Length == 2:
    {
        using var ec = Load();
        if (ec is null) return 1;
        var bytes = File.ReadAllBytes(args[1]);
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var entry = zip.GetEntry(PluginBundle.ManifestEntry);
        if (entry is null) { Console.Error.WriteLine("Kein plugin.json im Paket."); return 1; }
        JsonObject? manifest;
        using (var s = entry.Open()) manifest = JsonNode.Parse(s) as JsonObject;
        string F(string name) => manifest?[name] is JsonValue v && v.TryGetValue<string>(out var x) ? x : "";
        var kind = F("Kind") is { Length: > 0 } k ? k : PluginBundle.KindScript;
        var sha = StoreSignature.Sha256Hex(bytes);
        Console.WriteLine($"Art {kind} · Key {F("Key")} · Version {F("Version")} · SHA-256 {sha}");
        Console.WriteLine(Token(ec, StoreSignature.Text(kind, F("Key"), F("Version"), sha)));
        return 0;
    }
    default:
        return Usage();
}

ECDsa? Load()
{
    if (!File.Exists(keyPath)) { Console.Error.WriteLine($"Kein Schlüssel unter {keyPath} — erst „keygen“."); return null; }
    var ec = ECDsa.Create();
    ec.ImportFromPem(File.ReadAllText(keyPath));
    return ec;
}

static string Token(ECDsa ec, string text) => StoreSignature.KeyId(ec.ExportSubjectPublicKeyInfo()) + ":" + StoreSignature.Sign(text, ec);

static void PrintPublic(ECDsa ec)
{
    var spki = ec.ExportSubjectPublicKeyInfo();
    var id = StoreSignature.KeyId(spki);
    Console.WriteLine($"Schlüssel-Id: {id}");
    Console.WriteLine($"Öffentlicher Schlüssel: {Convert.ToBase64String(spki)}");
    Console.WriteLine($"Zeile für src/MatCMS.Shared/StoreSignature.cs (StoreKeys.Known): [\"{id}\"] = \"{Convert.ToBase64String(spki)}\",");
}

static int Usage()
{
    Console.Error.WriteLine("StoreSign keygen | pubkey | sign <kind> <key> <version> <sha256> | sign-package <bundle.zip>");
    return 2;
}
