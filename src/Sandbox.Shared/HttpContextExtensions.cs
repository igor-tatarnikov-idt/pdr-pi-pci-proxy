using Microsoft.AspNetCore.Http;

namespace Sandbox.Shared;

public static class HttpContextExtensions
{
    /// <summary>
    /// Where the request came from: the address and port of the peer that opened the connection (<c>127.0.0.1:51234</c>, an IPv6
    /// address in brackets), or <c>-</c> when there is none. A peer address, not an <c>X-Forwarded-For</c> claim: a caller could write that itself.
    /// </summary>
    public static string GetOrigin(this HttpContext context)
    {
        if (context.Connection.RemoteIpAddress is not { } address)
        {
            return "-";
        }
        var ip = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        var host = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{ip}]" : ip.ToString();
        return $"{host}:{context.Connection.RemotePort}";
    }
}
