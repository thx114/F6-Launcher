namespace HoYoShadeHub.Core.Networking;

/// <summary>Retired upstream services must not be revived by cached catalogs or saved proxy URLs.</summary>
public static class ForkNetworkPolicy
{
    public static void EnsureAllowed(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        string decoded = url;
        for (int i = 0; i < 3; i++) decoded = Uri.UnescapeDataString(decoded);
        // Inspect the complete URL too: a public proxy can contain a nested target URL.
        if (decoded.Contains("storage.hub.hoyosha.de", StringComparison.OrdinalIgnoreCase)
            || decoded.Contains("hoyoshadehub-glasses.pages.dev", StringComparison.OrdinalIgnoreCase))
            throw new HttpRequestException("This upstream private service is not available to F6 Launcher. Use direct GitHub or a public proxy.");
    }
}

internal sealed class ForkNetworkPolicyHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ForkNetworkPolicy.EnsureAllowed(request.RequestUri?.AbsoluteUri);
        HttpRequestMessage current = request;
        try
        {
            for (int redirects = 0; ; redirects++)
            {
                var response = await base.SendAsync(current, cancellationToken).ConfigureAwait(false);
                int status = (int)response.StatusCode;
                if (status is not (301 or 302 or 303 or 307 or 308) || response.Headers.Location is null)
                    return response;
                try
                {
                    if (redirects >= 10) throw new HttpRequestException("Too many HTTP redirects.");
                    var target = new Uri(current.RequestUri!, response.Headers.Location);
                    ForkNetworkPolicy.EnsureAllowed(target.AbsoluteUri);
                    if (target.Scheme is not ("https" or "http")) throw new HttpRequestException("Unsupported redirect scheme.");
                    if (current.Method != HttpMethod.Get && current.Method != HttpMethod.Head)
                        throw new HttpRequestException("Redirects for requests with content are not supported.");
                    var next = new HttpRequestMessage(current.Method, target) { Version = current.Version, VersionPolicy = current.VersionPolicy };
                    foreach (var header in current.Headers)
                        if (!header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase)
                            && (!header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) || target.Authority == current.RequestUri!.Authority))
                            next.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    if (!ReferenceEquals(current, request)) current.Dispose();
                    current = next;
                }
                finally { response.Dispose(); }
            }
        }
        finally { if (!ReferenceEquals(current, request)) current.Dispose(); }
    }
}
