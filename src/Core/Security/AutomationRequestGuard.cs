using System;
using System.Security.Cryptography;
using System.Text;

namespace ClopWindows.Core.Security;

/// <summary>Outcome of vetting an inbound request to the local automation HTTP API.</summary>
public enum AutomationRequestVerdict
{
    Allowed,

    /// <summary>The Host header is not one of our loopback names: DNS-rebinding attempt or misrouted proxy.</summary>
    ForbiddenHost,

    /// <summary>A browser sent the request (Origin / Sec-Fetch-* present). Cross-site pages must never drive the API.</summary>
    ForbiddenBrowserOrigin,

    Unauthorized,

    UnsupportedMediaType
}

/// <summary>
/// Request vetting for the loopback automation API. The API can optimise, replace and delete files, so it must
/// not be reachable by web pages (CSRF / DNS rebinding) or by unauthenticated local processes.
/// </summary>
public static class AutomationRequestGuard
{
    /// <summary>Largest JSON body the API reads. Optimise payloads are a list of paths, so 1 MiB is generous.</summary>
    public const int MaxBodyBytes = 1024 * 1024;

    /// <summary>256-bit random bearer token, hex encoded.</summary>
    public static string GenerateToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    public static bool IsAllowedHost(string? hostHeader, int port)
    {
        if (string.IsNullOrWhiteSpace(hostHeader))
        {
            return false;
        }

        var host = hostHeader.Trim();
        var suffix = ":" + port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return host.Equals("127.0.0.1" + suffix, StringComparison.OrdinalIgnoreCase)
            || host.Equals("localhost" + suffix, StringComparison.OrdinalIgnoreCase)
            || host.Equals("[::1]" + suffix, StringComparison.OrdinalIgnoreCase);
    }

    public static bool TokenMatches(string? authorizationHeader, string expectedToken)
    {
        if (string.IsNullOrWhiteSpace(expectedToken)
            || string.IsNullOrWhiteSpace(authorizationHeader)
            || !authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var presented = authorizationHeader[7..].Trim();
        if (presented.Length == 0)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(expectedToken));
    }

    public static AutomationRequestVerdict Evaluate(
        string method,
        string? hostHeader,
        string? originHeader,
        string? secFetchSiteHeader,
        string? authorizationHeader,
        string? contentType,
        string expectedToken,
        int port)
    {
        if (!IsAllowedHost(hostHeader, port))
        {
            return AutomationRequestVerdict.ForbiddenHost;
        }

        // Browsers attach Origin to every cross-origin POST and Sec-Fetch-Site to every request; native
        // automation clients (Power Automate, curl, PowerShell) send neither.
        if (!string.IsNullOrEmpty(originHeader) || !string.IsNullOrEmpty(secFetchSiteHeader))
        {
            return AutomationRequestVerdict.ForbiddenBrowserOrigin;
        }

        if (!TokenMatches(authorizationHeader, expectedToken))
        {
            return AutomationRequestVerdict.Unauthorized;
        }

        if (method.Equals("POST", StringComparison.OrdinalIgnoreCase)
            && (contentType is null || !contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase)))
        {
            return AutomationRequestVerdict.UnsupportedMediaType;
        }

        return AutomationRequestVerdict.Allowed;
    }
}
