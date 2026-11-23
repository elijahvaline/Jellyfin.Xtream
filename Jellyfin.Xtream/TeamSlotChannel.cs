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
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream;

/// <summary>
/// One team library. Jellyfin only learns about channels at startup, so a fixed number of slots is registered:
/// slot N shows the N-th enabled dynamic rule with "Show as team library" turned on, named after the team.
/// A slot is only visible when its rule is in use and, for rules linked to a team, on the team's game days.
/// </summary>
/// <param name="slot">The zero-based slot index.</param>
/// <param name="logger">Instance of the <see cref="ILogger"/> interface.</param>
/// <param name="sportsService">Instance of the <see cref="SportsService"/> class.</param>
/// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
public class TeamSlotChannel(int slot, ILogger<TeamSlotChannel> logger, SportsService sportsService, ILibraryManager libraryManager) : IChannel, IDisableMediaSourceDisplay
{
    /// <summary>
    /// The open token prefix that routes a media source to Jellyfin's Live TV media source provider:
    /// the MD5 of its type name, as Jellyfin computes it.
    /// </summary>
    private static readonly string LiveTvProviderKey = "Jellyfin.LiveTv.LiveTvMediaSourceProvider".GetMD5().ToString("N", CultureInfo.InvariantCulture);

    /// <summary>
    /// The number of team slots, i.e. the maximum number of team libraries.
    /// </summary>
    public const int SlotCount = 20;

    /// <inheritdoc />
    /// <remarks>
    /// Jellyfin derives the channel's id from its name, so each team keeps its own library (and image)
    /// even when rules are reordered. Unused slots get a fixed name and stay hidden.
    /// </remarks>
    public string? Name => GetSlotRule(out string name) is null ? $"Xtream Team Slot {slot + 1}" : name;

    /// <inheritdoc />
    public string? Description => "Every stream for this team, listed on game days.";

    /// <inheritdoc />
    /// <remarks>
    /// Jellyfin re-reads the items only when this changes: that is when the configuration changes, or when
    /// the streams listed right now change (new matches after a guide refresh, or a game day starting or ending).
    /// Working that out uses only cached matches, so it never contacts the provider or ESPN.
    /// </remarks>
    public string DataVersion
    {
        get
        {
            RuleMatches? matches = GetMatches();
            string listed = matches is null
                ? string.Empty
                : string.Join(',', ListedStreams(matches, DateTime.UtcNow).Select(s => $"{s.Stream.StreamId}:{s.Game?.Id}"));
            return Plugin.Instance.DataVersion + Name + DynamicChannelRule.StableHash(listed).ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <inheritdoc />
    public string HomePageUrl => string.Empty;

    /// <inheritdoc />
    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;

    /// <summary>
    /// Gets the rules shown as team libraries, in slot order, with unique library names.
    /// </summary>
    /// <returns>The rules and their library names.</returns>
    public static IReadOnlyList<(DynamicChannelRule Rule, string Name)> GetTeamRules()
    {
        List<(DynamicChannelRule Rule, string Name)> result = [];
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (DynamicChannelRule rule in Plugin.Instance.Configuration.LiveTvDynamic
            .Where(r => r.Enabled && r.ShowAsFolder && !string.IsNullOrWhiteSpace(r.Keyword))
            .DistinctBy(r => r.FolderKey)
            .Take(SlotCount))
        {
            string name = rule.DisplayFolderName;
            for (int n = 2; !names.Add(name); n++)
            {
                name = $"{rule.DisplayFolderName} ({n})";
            }

            result.Add((rule, name));
        }

        return result;
    }

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
        string? logo = type == ImageType.Primary ? GetMatches()?.Schedule?.Team.LogoUrl : null;
        return Task.FromResult(string.IsNullOrEmpty(logo)
            ? new DynamicImageResponse { HasImage = false }
            : new DynamicImageResponse { HasImage = true, Path = logo, Protocol = MediaProtocol.Http });
    }

    /// <inheritdoc />
    public IEnumerable<ImageType> GetSupportedChannelImages() => [ImageType.Primary];

    /// <inheritdoc />
    public bool IsEnabledFor(string userId)
    {
        try
        {
            if (GetSlotRule(out _) is not DynamicChannelRule rule)
            {
                return false;
            }

            if (!rule.HasTeam)
            {
                return true;
            }

            RuleMatches? matches = GetMatches();
            if (matches?.Schedule is null)
            {
                // Without a schedule (e.g. ESPN unreachable) there is no way to tell game days: show the library.
                return true;
            }

            DateTime now = DateTime.UtcNow;
            return matches.Streams.Any(s => s.Game is not null && SportsService.IsGameDay(s.Game, now));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to check whether team slot {Slot} is visible", slot + 1);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken)
    {
        try
        {
            if (!string.IsNullOrEmpty(query.FolderId) || GetSlotRule(out _) is not DynamicChannelRule rule)
            {
                return Result([]);
            }

            IReadOnlyList<RuleMatches> all = await sportsService.GetRuleMatchesAsync(cancellationToken).ConfigureAwait(false);
            RuleMatches? matches = all.FirstOrDefault(m => m.Rule.FolderKey == rule.FolderKey);
            return matches is null ? Result([]) : Result(CreateStreams(matches));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get items of team slot {Slot}", slot + 1);
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

    private static IEnumerable<MatchedChannel> ListedStreams(RuleMatches matches, DateTime nowUtc) =>
        matches.Streams
            .Where(s => s.Game is null || SportsService.IsGameDay(s.Game, nowUtc))
            .OrderBy(s => s.Game is null ? 1 : 0)
            .ThenBy(s => s.Game?.StartUtc)
            .ThenBy(s => s.Stream.Name, StringComparer.OrdinalIgnoreCase);

    private IEnumerable<ChannelItemInfo> CreateStreams(RuleMatches matches) =>
        ListedStreams(matches, DateTime.UtcNow).Select((s, index) => CreateStream(matches, s, index));

    private ChannelItemInfo CreateStream(RuleMatches matches, MatchedChannel match, int index)
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
            MediaSources = [CreateMediaSource(match.Stream.StreamId)],
        };
    }

    /// <summary>
    /// Creates the media source of a team stream. Played straight from the provider URL, Jellyfin knows nothing
    /// about the stream (codecs, resolution, bitrate) and treats it like a file, so the web player can take
    /// minutes to start. When the stream is also a Live TV channel, the source instead asks Jellyfin to open
    /// it through Live TV: the same restream and probe as playing the channel from the guide.
    /// </summary>
    private MediaSourceInfo CreateMediaSource(int streamId)
    {
        MediaSourceInfo source = StreamService.ForChannelItem(Plugin.Instance.StreamService.GetMediaSourceInfo(StreamType.Live, streamId));
        string externalId = StreamService.ToGuid(StreamService.LiveTvPrefix, streamId, 0, 0).ToString();

        // Jellyfin's internal Live TV channel id (LiveTvDtoService.GetInternalChannelId).
        Guid channelId = libraryManager.GetNewItemId($"{LiveTvService.ServiceName}{externalId}4".ToLowerInvariant(), typeof(LiveTvChannel));
        if (libraryManager.GetItemById(channelId) is not LiveTvChannel)
        {
            logger.LogDebug("Stream {StreamId} is not a Live TV channel; team item plays the provider URL", streamId);
            return source;
        }

        string channel = channelId.ToString("N", CultureInfo.InvariantCulture);
        source.RequiresOpening = true;
        source.RequiresClosing = true;
        source.OpenToken = $"{LiveTvProviderKey}_{nameof(LiveTvChannel)}_{channel}_{channel}";
        return source;
    }

    private DynamicChannelRule? GetSlotRule(out string name)
    {
        IReadOnlyList<(DynamicChannelRule Rule, string Name)> rules = GetTeamRules();
        if (slot < rules.Count)
        {
            name = rules[slot].Name;
            return rules[slot].Rule;
        }

        name = string.Empty;
        return null;
    }

    private RuleMatches? GetMatches()
    {
        if (GetSlotRule(out _) is not DynamicChannelRule rule)
        {
            return null;
        }

        return sportsService.GetRuleMatchesSnapshot().FirstOrDefault(m => m.Rule.FolderKey == rule.FolderKey);
    }
}
