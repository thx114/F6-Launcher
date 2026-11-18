using HoYoShadeHub.Extensions.Dlls;

namespace HoYoShadeHub.Extensions.Tests;

internal static class ModuleDllIdentityTests
{
    public static int Run()
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("Module identity: " + name);
            passed++;
            Console.WriteLine("[PASS] Module identity: " + name);
        }

        (string Id, string DllHint)[] shared =
        [
            ("dlssnr-amd", "version.dll"),
            ("dlssg-sm86", "version.dll"),
            ("mfg-unlock", "version.dll"),
            ("dlss-enabler", "version.dll"),
        ];
        Check(ModuleDllIdentity.FindUniqueCatalogId(@"C:\manual\version.dll", shared) is null,
            "shared version.dll remains a manual DLL");
        Check(ModuleDllIdentity.FindUniqueCatalogId(@"C:\manual\VERSION.DLL", shared.Reverse()) is null,
            "catalog ordering and filename case cannot select a product");
        Check(ModuleDllIdentity.FindUniqueCatalogId("version.dll", shared.Take(1)) is null,
            "partial catalog cannot identify a shared proxy");
        foreach (string proxy in new[] { "dxgi.dll", "winmm.dll", "dinput8.dll", "wininet.dll", "dbghelp.dll", "d3d11.dll", "d3d12.dll", "winhttp.dll" })
            Check(ModuleDllIdentity.FindUniqueCatalogId(proxy, [("unrelated", proxy)]) is null,
                proxy + " does not imply product identity");

        (string Id, string DllHint)[] named = [("veritas", "veritas.dll"), ("bridge", "Dx11FsrBridge.dll")];
        Check(ModuleDllIdentity.FindUniqueCatalogId(@"C:\manual\VERITAS.dll", named) == "veritas",
            "distinctive unambiguous name retains its catalog description");
        Check(ModuleDllIdentity.FindUniqueCatalogId("other.dll", named) is null, "unknown name remains unknown");
        Check(ModuleDllIdentity.FindUniqueCatalogId(null, named) is null, "absent path has no identity");
        Check(ModuleDllIdentity.FindUniqueCatalogId(" ", named) is null, "blank path has no identity");
        Check(ModuleDllIdentity.FindUniqueCatalogId("veritas.dll", [.. named, ("other", "veritas.dll")]) is null,
            "ambiguous distinctive name also remains unknown");
        Check(ModuleDllIdentity.FindUniqueCatalogId("veritas.dll", [.. named, ("VERITAS", "Veritas.dll")]) == "VERITAS",
            "duplicate hints for one catalog identity are not different products");
        Console.WriteLine($"PASS {passed} module identity checks");
        return 0;
    }
}
