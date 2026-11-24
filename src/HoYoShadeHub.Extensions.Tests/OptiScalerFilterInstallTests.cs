using HoYoShadeHub.Extensions.Services;
using HoYoShadeHub.Extensions.Dlls;
using HoYoShadeHub.Extensions.ReShade;
namespace HoYoShadeHub.Extensions.Tests;
internal static class OptiScalerFilterInstallTests
{
    public static int Run()
    {
        int checks = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); checks++; }
        string root = Path.Combine(Path.GetTempPath(), "f6-filters-" + Guid.NewGuid().ToString("N"));
        void Write(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); }
        try
        {
            string build = Path.Combine(root, "library", "F6", "v5.2");
            string source = Path.Combine(root, "package", "HoYoShade");
            string shader = "reshade-shaders/Shaders/iMMERSE/MartysMods_MXAO.fx";
            Write(Path.Combine(build, "OptiScaler.dll"), "F6");
            Write(Path.Combine(build, "nvngx_dlssnr.dll"), "NR");
            Write(Path.Combine(source, "ReShade64.dll"), "core");
            Write(Path.Combine(source, shader), "filter-v1");
            Write(Path.Combine(source, "reshade-shaders/Shaders/iMMERSE/MartysMods/Shared.fxh"), "include");
            Write(Path.Combine(source, "reshade-shaders/Textures/iMMERSE/noise.png"), "texture");
            Write(Path.Combine(source, "ReShade.ini"), "[GENERAL]\nEffectSearchPaths=D:/old/Shaders\n[USER]\nOption=keep\n");
            Write(Path.Combine(source, "inject.exe"), "exclude");
            var result = OptiScalerFilterInstaller.Install(build, source);
            Check(File.ReadAllText(Path.Combine(result.Directory, shader)) == "filter-v1", "Nested shader installed");
            Check(File.Exists(Path.Combine(result.Directory, "reshade-shaders/Shaders/iMMERSE/MartysMods/Shared.fxh")), "Headers preserved");
            Check(File.Exists(Path.Combine(result.Directory, "reshade-shaders/Textures/iMMERSE/noise.png")), "Nested textures preserved");
            Check(!File.Exists(Path.Combine(result.Directory, "inject.exe")), "Standalone injector excluded");
            var ini = IniDocument.Load(Path.Combine(result.Directory, "ReShade.ini"));
            Check(ini.GetValue("GENERAL", "EffectSearchPaths") == "reshade-shaders/Shaders/**", "Portable search path");
            Check(ini.GetValue("USER", "Option") == "keep", "Other INI settings preserved");
            Check(File.ReadAllText(Path.Combine(result.Directory, "Presets/BeforeSR-Default.ini")).Trim() == "Techniques=", "No filters enabled by default");
            Check(File.ReadAllText(Path.Combine(source, "ReShade.ini")).Contains("D:/old"), "Source untouched");
            string Runtime() => Path.Combine(Path.GetDirectoryName(NrRuntimeLibrary.PrepareOpti(Path.Combine(root, "cache"), "hk4e_cn", "F6/v5.2", Path.Combine(build, "OptiScaler.dll"), null))!, "HoYoShade");
            string runtime = Runtime();
            Check(File.Exists(Path.Combine(runtime, "ReShade64.dll")) && File.Exists(Path.Combine(runtime, shader)), "Full tree beside loaded DLL");
            Write(Path.Combine(runtime, "ReShade.ini"), "user-runtime-config");
            Write(Path.Combine(runtime, "Presets/BeforeSR-Default.ini"), "user-runtime-preset");
            Write(Path.Combine(source, shader), "filter-v2");
            OptiScalerFilterInstaller.Install(build, source);
            Check(File.ReadAllText(Path.Combine(runtime, shader)) == "filter-v1", "Install staging does not mutate linked running assets");
            Runtime();
            Check(File.ReadAllText(Path.Combine(runtime, shader)) == "filter-v2", "Shader update reaches existing cache");
            Check(File.ReadAllText(Path.Combine(runtime, "ReShade.ini")) == "user-runtime-config", "Runtime INI preserved");
            Check(File.ReadAllText(Path.Combine(runtime, "Presets/BeforeSR-Default.ini")) == "user-runtime-preset", "Runtime preset preserved");
            File.Delete(Path.Combine(source, "ReShade64.dll"));
            bool rejected = false;
            try { OptiScalerFilterInstaller.Install(build, source); } catch (FileNotFoundException) { rejected = true; }
            Check(rejected && File.ReadAllText(Path.Combine(result.Directory, shader)) == "filter-v2", "Incomplete source leaves install intact");
            Console.WriteLine($"PASS {checks} F6 filter install checks; no game started.");
            return 0;
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
