using Microsoft.Extensions.Configuration;

namespace Sandbox.Shared;

public static class ConfigurationExtensions
{
    /// <summary>Reads a base URL and guarantees a trailing slash, so relative request paths keep any path in it.</summary>
    public static Uri GetBaseUri(this IConfiguration configuration, string key) =>
        new(configuration[key]!.TrimEnd('/') + "/");
}
