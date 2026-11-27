// Copyright (C) 2022  Kevin Jilissen

// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.

// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// Checks for addresses handed to the server by the IPTV provider, so the provider cannot point the
/// server at the local network. This is a best-effort check inside the plugin (a host firewall that
/// stops the Jellyfin user from opening connections into the LAN is the real protection).
/// </summary>
public static class NetworkGuard
{
    private static readonly TimeSpan _hostCacheDuration = TimeSpan.FromHours(1);

    private static readonly ConcurrentDictionary<string, (bool IsPublic, DateTime Expires)> _hostCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Filters an image URL from the provider before Jellyfin downloads it: only http(s) URLs to public internet
    /// hosts are kept, so the provider cannot make the server fetch from the local network. Host lookups are cached.
    /// </summary>
    /// <param name="url">The image URL.</param>
    /// <returns>The same URL, or <c>null</c> when it is not safe.</returns>
    public static string? SafeImageUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        string host = uri.DnsSafeHost;
        DateTime now = DateTime.UtcNow;
        if (!_hostCache.TryGetValue(host, out var cached) || cached.Expires < now)
        {
            bool isPublic;
            TimeSpan duration = _hostCacheDuration;
            try
            {
                IPAddress[] addresses = IPAddress.TryParse(host, out IPAddress? literal) ? [literal] : Dns.GetHostAddresses(host);
                isPublic = addresses.Length > 0 && Array.TrueForAll(addresses, IsPublic);
            }
            catch (SocketException)
            {
                // Possibly a passing DNS failure: try again soon.
                isPublic = false;
                duration = TimeSpan.FromMinutes(5);
            }

            cached = (isPublic, now + duration);
            _hostCache[host] = cached;
        }

        return cached.IsPublic ? url : null;
    }

    /// <summary>
    /// Describes a URL for logs without its path or query, which carry the provider credentials.
    /// </summary>
    /// <param name="url">The URL.</param>
    /// <returns>The scheme, host and port only.</returns>
    public static string Describe(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ? $"{uri.Scheme}://{uri.Authority}" : "(invalid)";

    /// <summary>
    /// Checks whether a URL is http(s) and every address its host resolves to is on the public internet.
    /// </summary>
    /// <param name="uri">The URL.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether the URL is safe to fetch.</returns>
    public static async Task<bool> IsPublicHttpAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (!uri.IsAbsoluteUri || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        IPAddress[] addresses;
        if (IPAddress.TryParse(uri.DnsSafeHost, out IPAddress? literal))
        {
            addresses = [literal];
        }
        else
        {
            try
            {
                addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, cancellationToken).ConfigureAwait(false);
            }
            catch (SocketException)
            {
                return false;
            }
        }

        return addresses.Length > 0 && Array.TrueForAll(addresses, IsPublic);
    }

    /// <summary>
    /// Checks whether an address is on the public internet (not loopback, private, link-local, CGNAT, multicast, ...).
    /// </summary>
    /// <param name="address">The address.</param>
    /// <returns>Whether the address is public.</returns>
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] b = address.GetAddressBytes();
            return !(b[0] == 0
                || b[0] == 10
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || b[0] >= 224);
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            byte[] b = address.GetAddressBytes();
            return !(address.Equals(IPAddress.IPv6None)
                || address.IsIPv6LinkLocal
                || address.IsIPv6SiteLocal
                || address.IsIPv6Multicast
                || (b[0] & 0xfe) == 0xfc);
        }

        return false;
    }
}
