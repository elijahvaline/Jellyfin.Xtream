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
namespace Jellyfin.Xtream.Api.Models;

/// <summary>
/// The security status shown on the plugin's settings page.
/// </summary>
public class SecurityResponse
{
    /// <summary>
    /// Gets or sets a value indicating whether the plugin may connect to the IPTV provider.
    /// </summary>
    public bool IptvEnabled { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the provider is reached over HTTPS.
    /// </summary>
    public bool ProviderUsesHttps { get; set; }

    /// <summary>
    /// Gets or sets whether Jellyfin can connect into the local network, or <c>null</c> when unknown.
    /// </summary>
    public bool? LanReachable { get; set; }

    /// <summary>
    /// Gets or sets a description of the local network check.
    /// </summary>
    public string? LanDetail { get; set; }
}
