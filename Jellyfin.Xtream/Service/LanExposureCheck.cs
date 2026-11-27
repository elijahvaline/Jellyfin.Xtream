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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// Checks from inside Jellyfin whether Jellyfin can open connections into the local network, by trying to reach
/// the router. With a host firewall that confines Jellyfin to the internet, this fails; if it succeeds, a
/// compromised Jellyfin (e.g. through a malicious stream) could reach the other devices at home.
/// </summary>
public static class LanExposureCheck
{
    private static readonly int[] _ports = [80, 443, 53];

    private static LanExposureResult? _last;

    /// <summary>
    /// Gets the result of the most recent check, if any.
    /// </summary>
    public static LanExposureResult? Last => Volatile.Read(ref _last);

    /// <summary>
    /// Tries to connect to the default gateway (the router) on a few common ports.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The result.</returns>
    public static async Task<LanExposureResult> RunAsync(CancellationToken cancellationToken)
    {
        LanExposureResult result;
        IPAddress? gateway = GetDefaultGateway();
        if (gateway is null)
        {
            result = new LanExposureResult(null, null, "No default gateway found, so the local network could not be tested.");
        }
        else
        {
            int? open = null;
            foreach (int port in _ports)
            {
                using Socket socket = new(gateway.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                try
                {
                    await socket.ConnectAsync(gateway, port, timeout.Token).ConfigureAwait(false);
                    open = port;
                    break;
                }
                catch (Exception ex) when (ex is SocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    // Blocked, closed or timed out: try the next port.
                }
            }

            result = open is int port2
                ? new LanExposureResult(true, gateway.ToString(), $"Jellyfin could connect to the router ({gateway}:{port2}). Nothing stops a compromised Jellyfin from reaching your local network.")
                : new LanExposureResult(false, gateway.ToString(), $"Jellyfin could not connect to the router ({gateway}). Connections into the local network appear to be blocked.");
        }

        Volatile.Write(ref _last, result);
        return result;
    }

    private static IPAddress? GetDefaultGateway()
    {
        // Linux: /proc/net/route lists the gateway of the default route (destination 0) in little-endian hex.
        try
        {
            if (File.Exists("/proc/net/route"))
            {
                foreach (string line in File.ReadLines("/proc/net/route").Skip(1))
                {
                    string[] cols = line.Split('\t', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (cols.Length > 2 && cols[1] == "00000000"
                        && uint.TryParse(cols[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint gw) && gw != 0)
                    {
                        return new IPAddress(gw);
                    }
                }
            }
        }
        catch (IOException)
        {
            // Fall through.
        }

        return null;
    }
}
