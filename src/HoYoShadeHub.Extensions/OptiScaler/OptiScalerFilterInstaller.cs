using HoYoShadeHub.Extensions.ReShade;

namespace HoYoShadeHub.Extensions.Services;

/// <summary>Install F6's private filter tree beside its DLL, independently of standalone injection.</summary>
public static class OptiScalerFilterInstaller
{
    public sealed record Result(string Directory, int Files);

    // Independent stage bytes avoid writing through links into the installed tree.
    private static int CopyFiles(string source, string target)
    {
        PackageTree.ValidateTree(source);
        int count = 0;
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, true);
            count++;
        }
        return count;
    }

    public static Result Install(string buildDirectory, string sourceDirectory, bool useSourceConfiguration = true)
    {
        string build = Path.GetFullPath(buildDirectory);
        string source = Path.GetFullPath(sourceDirectory);
        if (!File.Exists(Path.Combine(build, "OptiScaler.dll")))
            throw new FileNotFoundException("指定构建缺少 OptiScaler.dll。", build);
        PackageTree.ValidateTree(source);
        foreach (string required in new[] { "ReShade64.dll", "reshade-shaders/Shaders", "reshade-shaders/Textures" })
        {
            string path = Path.Combine(source, required);
            if (!File.Exists(path) && !Directory.Exists(path))
                throw new FileNotFoundException("滤镜安装源不完整，请先安装 HoYoShade 框架和 ReShade 着色器：" + required, path);
        }
        if (!Directory.EnumerateFiles(Path.Combine(source, "reshade-shaders", "Shaders"), "*.fx", SearchOption.AllDirectories).Any())
            throw new InvalidDataException("滤镜安装源没有 .fx 着色器。请先安装 ReShade 着色器包。");
        string target = Path.Combine(build, "HoYoShade");
        if (source.Equals(target, StringComparison.OrdinalIgnoreCase)
            || source.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || target.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("滤镜安装源和目标不能重叠。");
        string stage = PackageTree.CreateStage(target);
        try
        {
            if (Directory.Exists(target)) CopyFiles(target, stage);
            File.Copy(Path.Combine(source, "ReShade64.dll"), Path.Combine(stage, "ReShade64.dll"), true);
            int files = 1;
            string[] folders = useSourceConfiguration
                ? ["reshade-shaders/Shaders", "reshade-shaders/Textures", "Presets"]
                : ["reshade-shaders/Shaders", "reshade-shaders/Textures"];
            foreach (string folder in folders)
                if (Directory.Exists(Path.Combine(source, folder)))
                    files += CopyFiles(Path.Combine(source, folder), Path.Combine(stage, folder));
            string iniPath = Path.Combine(source, "ReShade.ini");
            string stagedIni = Path.Combine(stage, "ReShade.ini");
            var ini = useSourceConfiguration && File.Exists(iniPath) ? IniDocument.Load(iniPath)
                : File.Exists(stagedIni) ? IniDocument.Load(stagedIni) : IniDocument.CreateEmpty();
            ini.SetValue("GENERAL", "EffectSearchPaths", "reshade-shaders/Shaders/**");
            ini.SetValue("GENERAL", "TextureSearchPaths", "reshade-shaders/Textures/**");
            ini.SetValue("GENERAL", "PresetPath", "Presets/BeforeSR-Default.ini");
            if (ini.GetValue("GENERAL", "PreprocessorDefinitions") is null)
                ini.SetValue("GENERAL", "PreprocessorDefinitions", "RESHADE_DEPTH_LINEARIZATION_FAR_PLANE=1000.0,RESHADE_DEPTH_INPUT_IS_UPSIDE_DOWN=0,RESHADE_DEPTH_INPUT_IS_REVERSED=1,RESHADE_DEPTH_INPUT_IS_LOGARITHMIC=0");
            ini.SetValue("OVERLAY", "TutorialProgress", "4");
            File.WriteAllLines(stagedIni, ini.Lines);
            string preset = Path.Combine(stage, "Presets", "BeforeSR-Default.ini");
            Directory.CreateDirectory(Path.GetDirectoryName(preset)!);
            if (!File.Exists(preset)) File.WriteAllText(preset, "Techniques=\n");
            PackageTree.Commit(stage, target);
            return new(target, files);
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }
}
