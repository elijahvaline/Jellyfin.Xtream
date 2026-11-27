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

using System.Collections.Generic;
using System.Collections.ObjectModel;
using MediaBrowser.Model.Plugins;

#pragma warning disable CA2227
namespace Jellyfin.Xtream.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets a value indicating whether the plugin may connect to the IPTV provider at all.
    /// When off, no request is sent to the provider, active streams are cut and the provider's
    /// channels and items disappear from the library at the next refresh.
    /// </summary>
    public bool IptvEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the base url including protocol and trailing slash.
    /// </summary>
    public string BaseUrl { get; set; } = "https://example.com";

    /// <summary>
    /// Gets or sets the username.
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the password.
    /// </summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the user agent override.
    /// </summary>
    public string UserAgent { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the Catch-up channel is visible.
    /// </summary>
    public bool IsCatchupVisible { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the Series channel is visible.
    /// </summary>
    public bool IsSeriesVisible { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the Video On-demand channel is visible.
    /// </summary>
    public bool IsVodVisible { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the Video On-demand channel is visible.
    /// </summary>
    public bool IsTmdbVodOverride { get; set; } = true;

    /// <summary>
    /// Gets or sets the channels displayed in Live TV.
    /// </summary>
    public SerializableDictionary<int, HashSet<int>> LiveTv { get; set; } = [];

    /// <summary>
    /// Gets or sets the streams displayed in VOD.
    /// </summary>
    public SerializableDictionary<int, HashSet<int>> Vod { get; set; } = [];

    /// <summary>
    /// Gets or sets the streams displayed in Series.
    /// </summary>
    public SerializableDictionary<int, HashSet<int>> Series { get; set; } = [];

    /// <summary>
    /// Gets or sets the channel override configuration for Live TV.
    /// </summary>
    public SerializableDictionary<int, ChannelOverrides> LiveTvOverrides { get; set; } = [];

    /// <summary>
    /// Gets or sets the keyword rules which dynamically add channels to Live TV.
    /// </summary>
    public Collection<DynamicChannelRule> LiveTvDynamic { get; set; } = [];

    /// <summary>
    /// Gets or sets the stream ids which dynamic rules must never add, even when their name matches.
    /// </summary>
    public Collection<int> LiveTvDynamicHidden { get; set; } = [];

    /// <summary>
    /// Gets or sets the IANA time zone (e.g. <c>America/Chicago</c>) used to decide which day a game is on
    /// and to display game times. When empty, the server's time zone is used.
    /// </summary>
    public string SportsTimeZone { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the TV guide is refreshed around the games of linked teams,
    /// in addition to its regular schedule.
    /// </summary>
    public bool SmartRefreshEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets how many minutes before a game starts the smart refresh runs.
    /// </summary>
    public int SmartRefreshBeforeMinutes { get; set; } = 60;

    /// <summary>
    /// Gets or sets how many minutes after a game starts the smart refresh runs again, to clean up.
    /// </summary>
    public int SmartRefreshAfterMinutes { get; set; } = 240;

    /// <summary>
    /// Gets or sets a value indicating whether team libraries are moved to the front of every user's library order.
    /// </summary>
    public bool TeamLibrariesFirst { get; set; } = true;
}
#pragma warning restore CA2227
