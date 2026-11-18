using HoYoShadeHub.Features.Xxmi;
using HoYoShadeHub.Extensions.Games;

namespace HoYoShadeHub.Extensions.Tests;

internal static class ModPreviewTests
{
    public static int Run(string? liveMod = null, string? liveConfig = null, string? liveBackend = null, string? liveCategory = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "mod-preview-" + Guid.NewGuid().ToString("N"));
        int passed = 0;
        void Check(bool result, string name)
        {
            if (!result) throw new InvalidOperationException(name);
            Console.WriteLine("PASS " + name);
            passed++;
        }
        string Put(string relative)
        {
            string path = Path.GetFullPath(Path.Combine(root, relative));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "fixture");
            return path;
        }
        try
        {
            Check(XxmiModManager.LocalPreview(null) is null, "missing mod path");
            Put("VodyanitsaHairADiffuse.png");
            Check(XxmiModManager.LocalPreview(root) is null, "material textures never become preview");
            string nested = Put("Summer Bikini/preview.png");
            Check(string.Equals(XxmiModManager.LocalPreview(root), nested, StringComparison.OrdinalIgnoreCase), "Rocket wrapper directory preview");
            string direct = Put("Preview.JPG");
            Check(string.Equals(XxmiModManager.LocalPreview(root), direct, StringComparison.OrdinalIgnoreCase), "root preview takes priority over nested PNG");
            File.Delete(direct);
            File.Delete(nested);
            string deeper = Put("Wrapper/Variant/Sub/preview.webp");
            Check(string.Equals(XxmiModManager.LocalPreview(root), deeper, StringComparison.OrdinalIgnoreCase), "three nested levels supported");
            string shallow = Put("Wrapper/preview.bmp");
            Check(string.Equals(XxmiModManager.LocalPreview(root), shallow, StringComparison.OrdinalIgnoreCase), "nearest preview wins");
            File.Delete(shallow);
            File.Delete(deeper);
            Put("Wrapper/Variant/Sub/TooDeep/preview.png");
            Check(XxmiModManager.LocalPreview(root) is null, "search depth bounded");

            string mod = Path.GetFullPath(Path.Combine(root, "Rocket/backing/Mods/SR/知更鸟•晴歌/Outfit #1 & Summer"));
            Directory.CreateDirectory(mod);
            string config = Put("Rocket/backing/config.ini");
            var context = new ModDirectoryContext("fixture", ModBackend.Rocket,
                Path.Combine(root, "Rocket/backing/Config/SR"), Path.GetDirectoryName(Path.GetDirectoryName(mod))!,
                ModDisableStyle.Prefix, [new(config, "fixture")]);
            string portrait = Put("Rocket/backing/asset/角色头像/SR/风/知更鸟•晴歌.png");
            Check(XxmiModManager.LocalPreview(context, mod, "知更鸟•晴歌") == portrait,
                "Rocket role group uses matching current-game character portrait");
            Check(XxmiModManager.LocalPreview(context, mod, "missing") is null,
                "unknown role does not select an arbitrary portrait");
            Check(XxmiModManager.LocalPreview(context with { Backend = ModBackend.Xxmi }, mod, "知更鸟•晴歌") is null,
                "XXMI never searches Rocket portrait directories");
            Check(XxmiModManager.LocalPreview(context with { ConfigStamps = [] }, mod, "知更鸟•晴歌") is null,
                "missing Rocket context leaves preview absent");
            Check(XxmiModManager.LocalPreview(context, mod, "../知更鸟•晴歌") is null,
                "category stays a leaf name");
            string own = Put("Rocket/backing/Mods/SR/知更鸟•晴歌/Outfit #1 & Summer/Wrapper/preview.webp");
            Check(XxmiModManager.LocalPreview(context, mod, "知更鸟•晴歌") == own,
                "mod-supplied cover wins over Rocket character portrait");
            File.Delete(own);
            string disabled = Path.Combine(root, "Rocket/backing/Mods/SR/DISABLED 知更鸟•晴歌");
            Directory.CreateDirectory(disabled);
            Check(XxmiModManager.LocalPreview(context, disabled, null) == portrait,
                "ungrouped disabled Rocket character uses its display name");
            File.Delete(portrait);
            Put("Rocket/backing/asset/角色头像/YS/风/知更鸟•晴歌.png");
            Check(XxmiModManager.LocalPreview(context, mod, "知更鸟•晴歌") is null,
                "same-name portrait in another game cannot leak into current card");
            portrait = Put("Rocket/backing/asset/角色头像/SR/知更鸟•晴歌.JPG");
            Check(string.Equals(XxmiModManager.LocalPreview(context, mod, "知更鸟•晴歌"), portrait, StringComparison.OrdinalIgnoreCase),
                "direct Rocket portrait and alternate image extension supported");
            if (liveMod is not null && liveConfig is not null && liveBackend is not null && liveCategory is not null)
            {
                var live = new ModDirectoryContext("read-only-probe", ModBackend.Rocket, liveBackend,
                    Path.GetDirectoryName(Path.GetDirectoryName(liveMod))!, ModDisableStyle.Prefix,
                    [new(liveConfig, "read-only")]);
                string? found = XxmiModManager.LocalPreview(live, liveMod, liveCategory);
                Check(found is not null && File.Exists(found), "reported live Rocket mod resolves an existing portrait without writes");
                Console.WriteLine("LIVE_PREVIEW=" + found);
            }
            Console.WriteLine($"Mod preview tests: PASS {passed}, FAIL 0");
            return 0;
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
