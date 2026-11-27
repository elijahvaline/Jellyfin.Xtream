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
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Service;

namespace Jellyfin.Xtream.Client;

/// <summary>
/// HTTP clients for everything the plugin fetches from the internet (provider API, streams, sports schedules).
/// Every connection, including each redirect hop, is checked when it is made: it only goes to public internet
/// addresses, so neither the provider nor a DNS trick can point the plugin at the local network.
/// </summary>
public static class GuardedHttp
{
    /// <summary>
    /// The largest API response accepted, so a provider cannot exhaust the server's memory.
    /// Large providers list tens of thousands of streams in one response, which stays well below this.
    /// </summary>
    public const long MaxApiResponseBytes = 256L * 1024 * 1024;

    /// <summary>
    /// Creates a client for API requests: bounded response size and a timeout.
    /// </summary>
    /// <returns>The client.</returns>
    public static HttpClient CreateApiClient() => new(CreateHandler(), disposeHandler: true)
    {
        MaxResponseContentBufferSize = MaxApiResponseBytes,
        Timeout = TimeSpan.FromSeconds(100),
    };

    /// <summary>
    /// Creates a client for endless live streams: no overall timeout, but connecting must not hang.
    /// </summary>
    /// <returns>The client.</returns>
    public static HttpClient CreateStreamClient() => new(CreateHandler(), disposeHandler: true)
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    /// <summary>
    /// Creates a client with the same User-Agent as Jellyfin's own default client, so providers see the same requests as before.
    /// </summary>
    /// <param name="factory">Jellyfin's HTTP client factory, only used to copy its User-Agent.</param>
    /// <param name="stream">Whether the client is used for endless live streams.</param>
    /// <returns>The client.</returns>
    public static HttpClient CreateLike(IHttpClientFactory factory, bool stream)
    {
        HttpClient client = stream ? CreateStreamClient() : CreateApiClient();
        foreach (var agent in factory.CreateClient(MediaBrowser.Common.Net.NamedClient.Default).DefaultRequestHeaders.UserAgent)
        {
            client.DefaultRequestHeaders.UserAgent.Add(agent);
        }

        return client;
    }

    private static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 5,
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(20),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),

        // A proxy would hide the real destination from the check below.
        UseProxy = false,
        ConnectCallback = ConnectPublicOnlyAsync,
    };

    private static async ValueTask<Stream> ConnectPublicOnlyAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        string host = context.DnsEndPoint.Host;
        IPAddress[] resolved = IPAddress.TryParse(host, out IPAddress? literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);

        // Connect to exactly the addresses that were checked, so the name cannot be re-resolved to a LAN address.
        IPAddress[] allowed = resolved.Where(NetworkGuard.IsPublic).ToArray();
        if (allowed.Length == 0)
        {
            throw new HttpRequestException($"Blocked a connection to {host}: it is not a public internet address.");
        }

        Socket socket = new(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
