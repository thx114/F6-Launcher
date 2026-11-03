using HoYoShadeHub.Core.Metadata.Github;
using HoYoShadeHub.Core.Networking;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace HoYoShadeHub.Core.Metadata;

public class MetadataClient
{


    private const string API_PREFIX_GITHUB = "https://raw.githubusercontent.com/thx114/HoYoShadeHub-DLSS5/main/metadata";

    private const string API_PREFIX_JSDELIVR = "https://cdn.jsdelivr.net/gh/thx114/HoYoShadeHub-DLSS5@main/metadata";


    private string API_PREFIX = API_PREFIX_GITHUB;

#if DEV
    private const string API_VERSION = "dev";
#else
    private const string API_VERSION = "v1";
#endif


    private readonly HttpClient _httpClient;


    public MetadataClient(int apiIndex = 0, HttpClient? httpClient = null)
    {
        SetApiPrefix(apiIndex);
        _httpClient = httpClient ?? new HttpClient(DohService.CreateSocketsHttpHandler()) { DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher };
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("F6Launcher-Metadata");
    }



    public void SetApiPrefix(int index)
    {
        API_PREFIX = index switch
        {
            1 => API_PREFIX_GITHUB,
            2 => API_PREFIX_JSDELIVR,
            _ => API_PREFIX_GITHUB,
        };
    }



    private async Task<T> CommonGetAsync<T>(string url, CancellationToken cancellationToken = default) where T : class
    {
        ForkNetworkPolicy.EnsureAllowed(url);
        var res = await _httpClient.GetFromJsonAsync(url, typeof(T), MetadataJsonContext.Default, cancellationToken) as T;
        if (res == null)
        {
            throw new NullReferenceException("");
        }
        else
        {
            return res;
        }
    }




    private string GetUrl(string suffix)
    {
        return $"{API_PREFIX.TrimEnd('/')}/{API_VERSION}/{suffix}";
    }




    public async Task<ReleaseVersion> GetVersionAsync(bool isPrerelease, Architecture architecture, CancellationToken cancellationToken = default)
    {
#if DEV
        isPrerelease = true;
#endif
        var name = (isPrerelease, architecture) switch
        {
            (false, Architecture.X64) => "version_stable_x64.json",
            (true, Architecture.X64) => "version_preview_x64.json",
            (false, Architecture.Arm64) => "version_stable_arm64.json",
            (true, Architecture.Arm64) => "version_preview_arm64.json",
            _ => throw new PlatformNotSupportedException($"{architecture} is not supported."),
        };
        var url = GetUrl(name);
        return await CommonGetAsync<ReleaseVersion>(url, cancellationToken);
    }



    public async Task<ReleaseVersion> GetReleaseAsync(bool isPrerelease, Architecture architecture, CancellationToken cancellationToken = default)
    {
#if DEV
        isPrerelease = true;
#endif
        var name = (isPrerelease, architecture) switch
        {
            (false, Architecture.X64) => "release_stable_x64.json",
            (true, Architecture.X64) => "release_preview_x64.json",
            (false, Architecture.Arm64) => "release_stable_arm64.json",
            (true, Architecture.Arm64) => "release_preview_arm64.json",
            _ => throw new PlatformNotSupportedException($"{architecture} is not supported."),
        };
        var url = GetUrl(name);
        return await CommonGetAsync<ReleaseVersion>(url, cancellationToken);
    }




    #region Github



    public async Task<GithubRelease?> GetGithubLatestReleaseAsync(CancellationToken cancellationToken = default)
    {
        const string url = "https://api.github.com/repos/thx114/HoYoShadeHub-DLSS5/releases?page=1&per_page=1";
        var list = await CommonGetAsync<List<GithubRelease>>(url, cancellationToken);
        return list?.FirstOrDefault();
    }



    public async Task<List<GithubRelease>> GetGithubReleaseAsync(int page, int perPage, CancellationToken cancellationToken = default)
    {
        string url = $"https://api.github.com/repos/thx114/HoYoShadeHub-DLSS5/releases?page={page}&per_page={perPage}";
        var list = await CommonGetAsync<List<GithubRelease>>(url, cancellationToken);
        return list ?? new List<GithubRelease>();
    }



    public async Task<GithubRelease?> GetGithubReleaseAsync(string tag, CancellationToken cancellationToken = default)
    {
        string url = $"https://api.github.com/repos/thx114/HoYoShadeHub-DLSS5/releases/tags/{tag}";
        return await CommonGetAsync<GithubRelease>(url, cancellationToken);
    }


    public async Task<string> RenderGithubMarkdownAsync(string markdown, CancellationToken cancellationToken = default)
    {
        const string url = "https://api.github.com/markdown";
        var request = new GithubMarkdownRequest
        {
            Text = markdown,
            Mode = "gfm",
            Context = "thx114/HoYoShadeHub-DLSS5",
        };
        var content = new StringContent(JsonSerializer.Serialize(request, typeof(GithubMarkdownRequest), MetadataJsonContext.Default), new MediaTypeHeaderValue("application/json"));
        var response = await _httpClient.PostAsync(url, content, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }



    #endregion



}
