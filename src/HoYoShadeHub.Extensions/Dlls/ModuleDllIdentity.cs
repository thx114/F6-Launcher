using HoYoShadeHub.Extensions.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HoYoShadeHub.Extensions.Dlls;

/// <summary>File-name hints for manual modules; shared proxy names never identify a product.</summary>
public static class ModuleDllIdentity
{
    public static string? FindUniqueCatalogId(string? dllPath,
        IEnumerable<(string Id, string DllHint)> catalog)
    {
        if (string.IsNullOrWhiteSpace(dllPath)) return null;
        string name = Path.GetFileName(dllPath);
        if (OptiScalerLibrary.ProxyDllNames.Contains(name, StringComparer.OrdinalIgnoreCase)
            || name.Equals("d3d11.dll", StringComparison.OrdinalIgnoreCase)
            || name.Equals("d3d12.dll", StringComparison.OrdinalIgnoreCase)
            || name.Equals("winhttp.dll", StringComparison.OrdinalIgnoreCase)) return null;

        string? matched = null;
        foreach ((string id, string hint) in catalog)
        {
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(hint)
                || !string.Equals(Path.GetFileName(hint), name, StringComparison.OrdinalIgnoreCase)) continue;
            if (matched is not null && !string.Equals(matched, id, StringComparison.OrdinalIgnoreCase)) return null;
            matched = id;
        }
        return matched;
    }
}
