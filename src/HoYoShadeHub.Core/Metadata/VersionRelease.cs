using System.Text.Json.Serialization;

namespace HoYoShadeHub.Core.Metadata;

public class ReleaseVersion : IJsonOnDeserialized
{

    public string Version { get; set; }


    public string Architecture { get; set; }


    public DateTimeOffset BuildTime { get; set; }


    public bool DisableAutoUpdate { get; set; }


    public string Install { get; set; }


    public long InstallSize { get; set; }


    public string InstallHash { get; set; }


    public string Portable { get; set; }


    public long PortableSize { get; set; }


    public string PortableHash { get; set; }


    public string SeparatePrefix { get; set; }


    public List<ReleaseFile> SeparateFiles { get; set; }


    [JsonIgnore]
    public string ReleasePage => $"https://github.com/thx114/HoYoShadeHub-DLSS5/releases/tag/v{Version.TrimStart('v', 'V')}";


    public void OnDeserialized()
    {
        if (SeparateFiles is not null)
        {
            string prefix;
            if (string.IsNullOrWhiteSpace(SeparatePrefix))
            {
                throw new InvalidOperationException("F6 metadata must provide an explicit SeparatePrefix; use the fork portable updater.");
            }
            else
            {
                prefix = SeparatePrefix;
            }
            foreach (var file in SeparateFiles)
            {
                if (string.IsNullOrWhiteSpace(file.Url))
                {
                    file.Url = new Uri(new Uri(prefix.TrimEnd('/') + "/"), file.Hash).AbsoluteUri;
                }
            }

        }
    }

}



public class ReleaseFile
{

    public string Path { get; set; }


    public long Size { get; set; }


    public string Hash { get; set; }


    public string Url { get; set; }

}
