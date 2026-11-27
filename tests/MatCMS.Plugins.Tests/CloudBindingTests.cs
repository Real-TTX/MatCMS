using MatCMS.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MatCMS.Plugins.Tests;

/// <summary>The binding cms.cloud (MatOS binds a site to a MatCMS.Cloud): read from its three variables, the internal address for the server,
/// nothing at all without the key.</summary>
public sealed class CloudBindingTests
{
    private static IConfiguration Config(Dictionary<string, string?> values) => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void Without_the_key_or_an_address_there_is_no_binding()
    {
        Assert.Null(CloudBinding.From(Config(new())));
        Assert.Null(CloudBinding.From(Config(new() { [CloudBinding.UrlVariable] = "https://cloud.example.org" })));
        Assert.Null(CloudBinding.From(Config(new() { [CloudBinding.KeyVariable] = "k" })));
    }

    [Fact]
    public void The_server_talks_to_the_internal_address_and_knows_both()
    {
        var b = CloudBinding.From(Config(new()
        {
            [CloudBinding.UrlVariable] = "https://cloud.example.org/",
            [CloudBinding.InternalUrlVariable] = "http://matos-matcmscloud-web:8080",
            [CloudBinding.KeyVariable] = " key ",
        }))!;
        Assert.Equal("http://matos-matcmscloud-web:8080", b.ServerUrl);
        Assert.Equal("key", b.Key);
        Assert.True(b.Names("https://cloud.example.org"));
        Assert.True(b.Names("http://matos-matcmscloud-web:8080/"));
        Assert.False(b.Names("https://other.example.org"));
        Assert.False(b.Names(""));

        // without an internal address the public one is the server's too
        var only = CloudBinding.From(Config(new() { [CloudBinding.UrlVariable] = "https://cloud.example.org", [CloudBinding.KeyVariable] = "k" }))!;
        Assert.Equal("https://cloud.example.org", only.ServerUrl);
    }
}
