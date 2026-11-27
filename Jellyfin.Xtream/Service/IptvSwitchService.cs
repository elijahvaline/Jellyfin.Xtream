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
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// Acts on the IPTV connection switch: when it is turned off, every open restream from the provider is cut
/// and every session playing provider content is stopped. Saving the configuration also queues the guide and
/// channel refreshes (see <see cref="Plugin.UpdateConfiguration"/>), which remove the provider's items when off
/// and bring them straight back when turned on again.
/// </summary>
/// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
/// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
/// <param name="channels">All registered channels; the ones from this plugin are used.</param>
/// <param name="logger">Instance of the <see cref="ILogger"/> interface.</param>
public sealed class IptvSwitchService(
    ISessionManager sessionManager,
    ILibraryManager libraryManager,
    IEnumerable<IChannel> channels,
    ILogger<IptvSwitchService> logger) : IHostedService
{
    private readonly Lazy<HashSet<string>> _channelNames = new(() => channels
        .Where(c => c.GetType().Assembly == typeof(Plugin).Assembly)
        .Select(c => c.Name)
        .ToHashSet(StringComparer.Ordinal));

    private bool _wasEnabled = true;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _wasEnabled = Plugin.IsIptvEnabled;
        if (!_wasEnabled)
        {
            logger.LogWarning("The IPTV connection is switched off: the provider will not be contacted");
        }

        _ = Task.Run(LogLanExposureAsync, CancellationToken.None);

        Plugin.Instance.ConfigurationChanged += OnConfigurationChanged;
        sessionManager.PlaybackStart += OnPlaybackStart;
        return Task.CompletedTask;
    }

    private async Task LogLanExposureAsync()
    {
        LanExposureResult lan = await LanExposureCheck.RunAsync(CancellationToken.None).ConfigureAwait(false);
        if (lan.Reachable == true)
        {
            logger.LogWarning("Security check: {Detail}", lan.Detail);
        }
        else
        {
            logger.LogInformation("Security check: {Detail}", lan.Detail);
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        Plugin.Instance.ConfigurationChanged -= OnConfigurationChanged;
        sessionManager.PlaybackStart -= OnPlaybackStart;
        return Task.CompletedTask;
    }

    private void OnConfigurationChanged(object? sender, BasePluginConfiguration e)
    {
        bool enabled = Plugin.IsIptvEnabled;
        bool was = _wasEnabled;
        _wasEnabled = enabled;
        if (enabled == was)
        {
            return;
        }

        if (enabled)
        {
            logger.LogInformation("The IPTV connection was switched on: refreshing the guide and channels from the provider");
            return;
        }

        int cut = Restream.CutAll();
        logger.LogWarning("The IPTV connection was switched off: cut {Count} restreams; removing provider items", cut);
        _ = Task.Run(StopSessionsAsync);
    }

    /// <summary>
    /// Items saved in the library keep their provider URL, and Jellyfin plays them without asking the plugin.
    /// While the connection is off, such playback (e.g. from favorites or Continue Watching) is stopped at once.
    /// </summary>
    private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
    {
        if (Plugin.IsIptvEnabled || e.Session is not SessionInfo session || e.Item is not BaseItem item || !IsFromProvider(item))
        {
            return;
        }

        _ = Task.Run(() => StopAsync(session, item));
    }

    private async Task StopSessionsAsync()
    {
        foreach (SessionInfo session in sessionManager.Sessions.ToList())
        {
            if (session.NowPlayingItem is not null
                && libraryManager.GetItemById(session.NowPlayingItem.Id) is BaseItem item
                && IsFromProvider(item))
            {
                await StopAsync(session, item).ConfigureAwait(false);
            }
        }
    }

    private async Task StopAsync(SessionInfo session, BaseItem item)
    {
        try
        {
            logger.LogInformation("Stopping playback of {Item} on {Device}: the IPTV connection is switched off", item.Name, session.DeviceName);
            await sessionManager.SendPlaystateCommand(
                null,
                session.Id,
                new PlaystateRequest { Command = PlaystateCommand.Stop },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to stop playback of {Item} on {Device}", item.Name, session.DeviceName);
        }
    }

    private bool IsFromProvider(BaseItem item)
    {
        if (item.ServiceName == LiveTvService.ServiceName)
        {
            return true;
        }

        if (item.ChannelId.Equals(Guid.Empty))
        {
            return false;
        }

        BaseItem? parent = libraryManager.GetItemById(item.ChannelId);
        return parent is LiveTvChannel channel
            ? channel.ServiceName == LiveTvService.ServiceName
            : parent is not null && _channelNames.Value.Contains(parent.Name);
    }
}
