using System.Security.Cryptography;
using MatCMS.Shared;
using Xunit;

namespace MatCMS.Cloud.Tests;

/// <summary>The signature of a MatCMS Store package (<see cref="StoreSignature"/>): only exactly the signed values with a trusted key pass.</summary>
public sealed class StoreSignatureTests
{
    private static (ECDsa Key, Dictionary<string, string> Keys, string Id) TestKey()
    {
        var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var spki = ec.ExportSubjectPublicKeyInfo();
        var id = StoreSignature.KeyId(spki);
        return (ec, new Dictionary<string, string> { [id] = Convert.ToBase64String(spki) }, id);
    }

    [Fact]
    public void A_signed_package_passes_and_every_changed_value_fails()
    {
        var (ec, keys, id) = TestKey();
        var package = "PK fake zip"u8.ToArray();
        var sha = StoreSignature.Sha256Hex(package);
        var sig = StoreSignature.Sign(StoreSignature.Text("native", "Forum", "1.4", sha), ec);

        Assert.True(StoreSignature.VerifyPackage(package, "native", "forum", "1.4", sha, sig, id, keys));          // key and kind compare lower-case
        Assert.True(StoreSignature.VerifyPackage(package, "NATIVE", "forum", "1.4", null, sig, id, keys));         // no hash given: the package's own
        Assert.False(StoreSignature.VerifyPackage(package, "script", "forum", "1.4", sha, sig, id, keys));         // another kind
        Assert.False(StoreSignature.VerifyPackage(package, "native", "verein", "1.4", sha, sig, id, keys));        // another key
        Assert.False(StoreSignature.VerifyPackage(package, "native", "forum", "1.5", sha, sig, id, keys));         // another version
        Assert.False(StoreSignature.VerifyPackage("PK changed"u8.ToArray(), "native", "forum", "1.4", null, sig, id, keys));   // another package
        Assert.False(StoreSignature.VerifyPackage(package, "native", "forum", "1.4", new string('0', 64), sig, id, keys));     // hash does not match
        Assert.False(StoreSignature.VerifyPackage(package, "native", "forum", "1.4", sha, sig, "0000000000000000", keys));    // unknown key
        Assert.False(StoreSignature.VerifyPackage(package, "native", "forum", "1.4", sha, "not base64!", id, keys));          // malformed
        Assert.False(StoreSignature.VerifyPackage(package, "native", "forum", "1.4", sha, sig, id));                          // not a shipped key

        var (other, _, _) = TestKey();
        var forged = StoreSignature.Sign(StoreSignature.Text("native", "forum", "1.4", sha), other);
        Assert.False(StoreSignature.VerifyPackage(package, "native", "forum", "1.4", sha, forged, id, keys));                 // somebody else's key
    }

    [Fact]
    public void The_shipped_keys_are_well_formed_P256_keys_named_by_their_id()
    {
        Assert.NotEmpty(StoreKeys.Known);
        foreach (var (id, spki) in StoreKeys.Known)
        {
            var bytes = Convert.FromBase64String(spki);
            Assert.Equal(id, StoreSignature.KeyId(bytes));
            using var ec = ECDsa.Create();
            ec.ImportSubjectPublicKeyInfo(bytes, out _);
            Assert.Equal(256, ec.KeySize);
        }
    }
}
