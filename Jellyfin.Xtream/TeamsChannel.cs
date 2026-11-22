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
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Configuration;
using Jellyfin.Xtream.Service;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream;

/// <summary>
/// A channel with a folder per dynamic rule that has "Show as folder" enabled, listing every stream the rule matches.
/// Streams of a linked team's games are only listed on the day of the game.
/// </summary>
/// <param name="logger">Instance of the <see cref="ILogger"/> interface.</param>
/// <param name="sportsService">Instance of the <see cref="SportsService"/> class.</param>
public class TeamsChannel(ILogger<TeamsChannel> logger, SportsService sportsService) : IChannel, IDisableMediaSourceDisplay
{
    /// <summary>
    /// How often the folder contents are refreshed, so game-day streams appear and disappear on time.
    /// </summary>
    private static readonly TimeSpan _refreshInterval = TimeSpan.FromMinutes(15);

    /// <inheritdoc />
    public string? Name => "Xtream Teams";

    /// <inheritdoc />
    public string? Description => "Every stream for your teams, grouped per team.";

    /// <inheritdoc />
    public string DataVersion =>
        Plugin.Instance.DataVersion + (DateTime.UtcNow.Ticks / _refreshInterval.Ticks).ToString(CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public string HomePageUrl => string.Empty;

    /// <inheritdoc />
    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;

    /// <inheritdoc />
    public InternalChannelFeatures GetChannelFeatures()
    {
        return new InternalChannelFeatures
        {
            ContentTypes = [
                ChannelMediaContentType.TvExtra,
            ],
            MediaTypes = [
                ChannelMediaType.Video
            ],
        };
    }

    /// <inheritdoc />
    public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken)
    {
        throw new ArgumentException("Unsupported image type: " + type);
    }

    /// <inheritdoc />
    public IEnumerable<ImageType> GetSupportedChannelImages() => [];

    /// <inheritdoc />
    public bool IsEnabledFor(string userId)
    {
        return Plugin.Instance.Configuration.LiveTvDynamic.Any(r => r.Enabled && r.ShowAsFolder);
    }

    /// <inheritdoc />
    public async Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<RuleMatches> matches = await sportsService.GetRuleMatchesAsync(cancellationToken).ConfigureAwait(false);
            List<RuleMatches> folders = matches.Where(m => m.Rule.ShowAsFolder).ToList();

            if (string.IsNullOrEmpty(query.FolderId))
            {
                return Result(folders.Select(CreateFolder).DistinctBy(f => f.Id));
            }

            StreamService.FromGuid(Guid.Parse(query.FolderId), out int prefix, out int folderKey, out int _, out int _);
            RuleMatches? folder = prefix == StreamService.TeamFolderPrefix
                ? folders.FirstOrDefault(f => f.Rule.FolderKey == folderKey)
                : null;
            return folder is null ? Result([]) : Result(CreateStreams(folder));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get team channel items");
            throw;
        }
    }

    private static ChannelItemResult Result(IEnumerable<ChannelItemInfo> items)
    {
        List<ChannelItemInfo> list = items.ToList();
        return new ChannelItemResult()
        {
            Items = list,
            TotalRecordCount = list.Count,
        };
    }

    private static ChannelItemInfo CreateFolder(RuleMatches matches)
    {
        return new ChannelItemInfo()
        {
            Id = StreamService.ToGuid(StreamService.TeamFolderPrefix, matches.Rule.FolderKey, 0, 0).ToString(),
            Name = matches.Rule.DisplayFolderName,
            ImageUrl = matches.Schedule?.Team.LogoUrl,
            Type = ChannelItemType.Folder,
            FolderType = ChannelFolderType.Container,
        };
    }

    private static IEnumerable<ChannelItemInfo> CreateStreams(RuleMatches matches)
    {
        DateTime now = DateTime.UtcNow;
        return matches.Streams
            .Where(s => s.Game is null || SportsService.IsGameDay(s.Game, now))
            .OrderBy(s => s.Game is null ? 1 : 0)
            .ThenBy(s => s.Game?.StartUtc)
            .ThenBy(s => s.Stream.Name, StringComparer.OrdinalIgnoreCase)
            .Select((s, index) => CreateStream(matches, s, index));
    }

    private static ChannelItemInfo CreateStream(RuleMatches matches, MatchedChannel match, int index)
    {
        SportsGame? game = match.Game;
        string title = StreamService.ParseName(match.Stream.Name).Title;
        string? overview = null;
        if (game is not null)
        {
            title = $"{SportsService.FormatStart(game, includeDay: false)} · {title}";
            overview = $"{game.Name}\n{SportsService.FormatStart(game, includeDay: true)}"
                + (string.IsNullOrEmpty(game.Broadcast) ? string.Empty : $" on {game.Broadcast}");
        }

        // Jellyfin only sets an item's name when it is first created, so include the game in the id:
        // each new game becomes a new item with an up-to-date title, and old ones are cleaned up.
        int gameKey = game is null ? 0 : DynamicChannelRule.StableHash(game.Id);
        return new ChannelItemInfo()
        {
            Id = StreamService.ToGuid(StreamService.TeamStreamPrefix, matches.Rule.FolderKey, match.Stream.StreamId, gameKey).ToString(),
            Name = title,
            Overview = overview,
            IndexNumber = index + 1,
            ImageUrl = string.IsNullOrEmpty(match.Stream.StreamIcon) ? matches.Schedule?.Team.LogoUrl : match.Stream.StreamIcon,
            ContentType = ChannelMediaContentType.TvExtra,
            MediaType = ChannelMediaType.Video,
            Type = ChannelItemType.Media,
            IsLiveStream = true,
            StartDate = game?.StartUtc,
            EndDate = game?.EndUtc,
            MediaSources = [Plugin.Instance.StreamService.GetMediaSourceInfo(StreamType.Live, match.Stream.StreamId)],
        };
    }
}
