using MatCMS.Plugin;

namespace StoreEinblick;

/// <summary>Store-Einblick (native variant): the same plugin as the script one — <see cref="SeShowcase"/> is compiled from its file.</summary>
public sealed class StoreEinblickPlugin : IPlugin
{
    public void Configure(IPluginBuilder b) => SeShowcase.Configure(b);
}
