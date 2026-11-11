namespace MatCMS.Plugin.Verein;

/// <summary>
/// A person's profile picture: uploaded by the member in the profile or by the board in the admin (children too), shrunk in the browser before it is
/// sent (a phone photo of several MB becomes a JPEG of ~100 KB — the plugin has no image library), stored in the plugin's private files and served
/// only to signed-in members who may see that person. The bytes are checked: only JPEG, PNG or WebP, whatever the file name says.
/// </summary>
public static class VereinPhoto
{
    public const long MaxBytes = 8 * 1024 * 1024;

    /// <summary>The picture's kind from its first bytes, or "" when it is none of the three.</summary>
    internal static string Sniff(byte[] b)
    {
        if (b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return ".jpg";
        if (b.Length > 8 && b[0] == 0x89 && b[1] == (byte)'P' && b[2] == (byte)'N' && b[3] == (byte)'G') return ".png";
        if (b.Length > 12 && b[0] == (byte)'R' && b[1] == (byte)'I' && b[2] == (byte)'F' && b[3] == (byte)'F' && b[8] == (byte)'W' && b[9] == (byte)'E' && b[10] == (byte)'B' && b[11] == (byte)'P') return ".webp";
        return "";
    }

    /// <summary>Stores the uploaded picture for the person (replacing an earlier one). Returns an error text, or null.</summary>
    public static async Task<string?> SaveAsync(IPluginContext ctx, VereinStore store, Person p, UploadedFile? up, CancellationToken ct)
    {
        if (up is null || up.Length == 0) return ctx.T("verein.photo.choose");
        if (up.Length > MaxBytes) return ctx.T("verein.photo.tooBig");
        byte[] bytes;
        using (var s = up.OpenRead()) using (var ms = new MemoryStream())
        {
            await s.CopyToAsync(ms, ct);
            bytes = ms.ToArray();
        }
        var ext = Sniff(bytes);
        if (ext.Length == 0) return ctx.T("verein.photo.type");
        var file = "a/" + Guid.NewGuid().ToString("N") + ext;
        using (var ms = new MemoryStream(bytes)) await ctx.Files.SaveAsync(file, ms, ct);
        if (p.Photo.Length > 0) await ctx.Files.DeleteAsync(p.Photo, ct);
        await store.People.UpdateAsync(p.Id, new Dictionary<string, object?> { ["photo"] = file }, ct);
        return null;
    }

    public static async Task RemoveAsync(IPluginContext ctx, VereinStore store, Person p, CancellationToken ct)
    {
        if (p.Photo.Length == 0) return;
        await ctx.Files.DeleteAsync(p.Photo, ct);
        await store.People.UpdateAsync(p.Id, new Dictionary<string, object?> { ["photo"] = "" }, ct);
    }

    public static Task<PluginResult> ServeAsync(IPluginContext ctx, Person p, CancellationToken ct) =>
        VereinPublic.ServeAsync(ctx, p.Photo, "profilbild" + Path.GetExtension(p.Photo), "", ct);

    /// <summary>The round picture, or the initials when there is none.</summary>
    public static string Avatar(Person p, string url, string initials, int size = 42)
    {
        var style = size == 42 ? "" : " style=\"width:" + size + "px;height:" + size + "px\"";
        return p.Photo.Length > 0
            ? "<span class=\"vm-avatar has-photo\"" + style + "><img src=\"" + V.H(url) + "?v=" + V.H(Path.GetFileNameWithoutExtension(p.Photo)[..8]) + "\" alt=\"\" loading=\"lazy\"></span>"
            : "<span class=\"vm-avatar\"" + style + ">" + V.H(initials) + "</span>";
    }

    /// <summary>
    /// Shrinks the chosen picture in the browser to 800 px (JPEG) and puts it back into the file field before the form is sent; shows a preview.
    /// Without canvas or DataTransfer (very old browsers) the original goes as it is.
    /// </summary>
    public const string Script = """
<script>(function(){document.querySelectorAll('input[type=file][data-vm-photo]').forEach(function(inp){
inp.addEventListener('change',function(){var f=inp.files&&inp.files[0];if(!f||!window.DataTransfer||!window.createImageBitmap)return;
createImageBitmap(f,{imageOrientation:'from-image'}).then(function(bmp){var max=800,s=Math.min(1,max/Math.max(bmp.width,bmp.height));
var c=document.createElement('canvas');c.width=Math.round(bmp.width*s);c.height=Math.round(bmp.height*s);c.getContext('2d').drawImage(bmp,0,0,c.width,c.height);
c.toBlob(function(b){if(!b)return;var dt=new DataTransfer();dt.items.add(new File([b],'profilbild.jpg',{type:'image/jpeg'}));inp.files=dt.files;
var pv=document.querySelector(inp.getAttribute('data-vm-photo'));if(pv){pv.innerHTML='';var i=new Image();i.src=URL.createObjectURL(b);pv.appendChild(i);pv.classList.add('has-photo');}},'image/jpeg',0.85);
}).catch(function(){});});});})();</script>
""";
}
