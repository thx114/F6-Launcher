namespace BuildTool;

internal static class ReleaseEndpoints
{
    // Legacy manifest publishing requires an explicitly maintained backend.
    internal static string BaseUrl => (Environment.GetEnvironmentVariable("F6_RELEASE_BASE_URL")
        ?? throw new InvalidOperationException("Set F6_RELEASE_BASE_URL to your own release backend; upstream services are not available to this fork.")).TrimEnd('/');
}
