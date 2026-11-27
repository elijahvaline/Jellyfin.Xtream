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
using System.Net.Http;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Api.Models;
using Jellyfin.Xtream.Client;
using Jellyfin.Xtream.Client.Models;
using Jellyfin.Xtream.Configuration;
using Jellyfin.Xtream.Service;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Jellyfin.Xtream.Api;

/// <summary>
/// The Jellyfin Xtream configuration API.
/// </summary>
/// <param name="xtreamClient">Instance of the <see cref="IXtreamClient"/> interface.</param>
/// <param name="sportsService">Instance of the <see cref="SportsService"/> class.</param>
/// <param name="smartRefreshService">Instance of the <see cref="SmartRefreshService"/> class.</param>
[ApiController]
[Route("[controller]")]
[Produces(MediaTypeNames.Application.Json)]
[IptvDisabledFilter]
public class XtreamController(IXtreamClient xtreamClient, SportsService sportsService, SmartRefreshService smartRefreshService) : ControllerBase
{
    private static CategoryResponse CreateCategoryResponse(Category category) =>
        new()
        {
            Id = category.CategoryId,
            Name = category.CategoryName,
        };

    private static ItemResponse CreateItemResponse(StreamInfo stream) =>
        new()
        {
            Id = stream.StreamId,
            Name = stream.Name,
            HasCatchup = stream.TvArchive,
            CatchupDuration = stream.TvArchiveDuration,
        };

    private static ItemResponse CreateItemResponse(Series series) =>
        new()
        {
            Id = series.SeriesId,
            Name = series.Name,
            HasCatchup = false,
            CatchupDuration = 0,
        };

    private static ChannelResponse CreateChannelResponse(StreamInfo stream) =>
        new()
        {
            Id = stream.StreamId,
            LogoUrl = NetworkGuard.SafeImageUrl(stream.StreamIcon) ?? string.Empty,
            Name = stream.Name,
            Number = stream.Num,
            CategoryId = stream.CategoryId,
        };

    /// <summary>
    /// Gets whether the plugin may connect to the IPTV provider.
    /// </summary>
    /// <returns>The state of the IPTV connection.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("Connection")]
    public ActionResult<ConnectionResponse> GetConnection()
    {
        return Ok(new ConnectionResponse { Enabled = Plugin.IsIptvEnabled });
    }

    /// <summary>
    /// Switches the connection to the IPTV provider on or off. Off: nothing is sent to the provider, open streams
    /// are cut and the provider's items are removed. On: the guide and channels are refreshed from the provider at once.
    /// </summary>
    /// <param name="enabled">Whether the plugin may connect to the provider.</param>
    /// <returns>The new state of the IPTV connection.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpPost("Connection")]
    public ActionResult<ConnectionResponse> SetConnection([FromQuery] bool enabled)
    {
        Plugin plugin = Plugin.Instance;
        PluginConfiguration config = plugin.Configuration;
        config.IptvEnabled = enabled;

        // Saves, raises ConfigurationChanged (cuts streams when off) and queues the guide and channel refreshes.
        plugin.UpdateConfiguration(config);
        return Ok(new ConnectionResponse { Enabled = Plugin.IsIptvEnabled });
    }

    /// <summary>
    /// Gets the security status: IPTV connection, HTTPS use and whether Jellyfin can reach the local network.
    /// </summary>
    /// <param name="recheck">Whether to run the local network check again.</param>
    /// <param name="cancellationToken">The cancellation token for cancelling requests.</param>
    /// <returns>The security status.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("Security")]
    public async Task<ActionResult<SecurityResponse>> GetSecurity([FromQuery] bool recheck, CancellationToken cancellationToken)
    {
        LanExposureResult lan = !recheck && LanExposureCheck.Last is LanExposureResult last
            ? last
            : await LanExposureCheck.RunAsync(cancellationToken).ConfigureAwait(false);
        return Ok(new SecurityResponse
        {
            IptvEnabled = Plugin.IsIptvEnabled,
            ProviderUsesHttps = Uri.TryCreate(Plugin.Instance.Configuration.BaseUrl, UriKind.Absolute, out Uri? baseUri)
                && baseUri.Scheme == Uri.UriSchemeHttps,
            LanReachable = lan.Reachable,
            LanDetail = lan.Detail,
        });
    }

    /// <summary>
    /// Checks whether the provider can be reached over HTTPS with a valid certificate, and optionally switches to it.
    /// </summary>
    /// <param name="apply">Whether to switch the base URL to HTTPS when it works.</param>
    /// <param name="cancellationToken">The cancellation token for cancelling requests.</param>
    /// <returns>The result of the check.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpPost("Security/Https")]
    public async Task<ActionResult<HttpsCheckResponse>> CheckHttps([FromQuery] bool apply, CancellationToken cancellationToken)
    {
        Plugin plugin = Plugin.Instance;
        PluginConfiguration config = plugin.Configuration;
        if (!Uri.TryCreate(config.BaseUrl, UriKind.Absolute, out Uri? current))
        {
            return Ok(new HttpsCheckResponse { Detail = "The base URL is not a valid URL." });
        }

        if (current.Scheme == Uri.UriSchemeHttps)
        {
            return Ok(new HttpsCheckResponse { Available = true, Url = config.BaseUrl, Detail = "The provider is already reached over HTTPS." });
        }

        // Same port first (some providers serve both on one port), then the standard HTTPS port.
        List<string> candidates = [];
        if (!current.IsDefaultPort)
        {
            candidates.Add($"https://{current.Host}:{current.Port}");
        }

        candidates.Add($"https://{current.Host}");
        foreach (string candidate in candidates)
        {
            try
            {
                using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                PlayerApi info = await xtreamClient.GetUserAndServerInfoAsync(
                    new ConnectionInfo(candidate, config.Username, config.Password),
                    timeout.Token).ConfigureAwait(false);
                if (info.UserInfo.Auth != 1)
                {
                    continue;
                }

                bool applied = false;
                if (apply)
                {
                    config.BaseUrl = candidate;
                    plugin.UpdateConfiguration(config);
                    applied = true;
                }

                return Ok(new HttpsCheckResponse
                {
                    Available = true,
                    Applied = applied,
                    Url = candidate,
                    Detail = applied
                        ? $"Switched to {candidate}. The guide and channels are being refreshed."
                        : $"The provider works over HTTPS at {candidate}.",
                });
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                // Not available on this address: try the next one.
            }
        }

        return Ok(new HttpsCheckResponse
        {
            Detail = "The provider does not answer over HTTPS with a valid certificate. Keep using HTTP, or ask the provider for an HTTPS address.",
        });
    }

    /// <summary>
    /// Test the configured provider.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token for cancelling requests.</param>
    /// <returns>An enumerable containing the categories.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("TestProvider")]
    public async Task<ActionResult<ProviderTestResponse>> TestProvider(CancellationToken cancellationToken)
    {
        Plugin plugin = Plugin.Instance;
        PlayerApi info = await xtreamClient.GetUserAndServerInfoAsync(plugin.Creds, cancellationToken).ConfigureAwait(false);
        return Ok(new ProviderTestResponse()
        {
            ActiveConnections = info.UserInfo.ActiveCons,
            ExpiryDate = info.UserInfo.ExpDate,
            MaxConnections = info.UserInfo.MaxConnections,
            ServerTime = info.ServerInfo.TimeNow,
            ServerTimezone = info.ServerInfo.Timezone,
            Status = info.UserInfo.Status,
            SupportsMpegTs = info.UserInfo.AllowedOutputFormats.Contains("ts"),
        });
    }

    /// <summary>
    /// Get all Live TV categories.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token for cancelling requests.</param>
    /// <returns>An enumerable containing the categories.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("LiveCategories")]
    public async Task<ActionResult<IEnumerable<CategoryResponse>>> GetLiveCategories(CancellationToken cancellationToken)
    {
        Plugin plugin = Plugin.Instance;
        List<Category> categories = await xtreamClient.GetLiveCategoryAsync(plugin.Creds, cancellationToken).ConfigureAwait(false);
        return Ok(categories.Select(CreateCategoryResponse));
    }

    /// <summary>
    /// Get all Live TV streams for the given category.
    /// </summary>
    /// <param name="categoryId">The category for which to fetch the streams.</param>
    /// <param name="cancellationToken">The cancellation token for cancelling requests.</param>
    /// <returns>An enumerable containing the streams.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("LiveCategories/{categoryId}")]
    public async Task<ActionResult<IEnumerable<StreamInfo>>> GetLiveStreams(int categoryId, CancellationToken cancellationToken)
    {
        Plugin plugin = Plugin.Instance;
        List<StreamInfo> streams = await xtreamClient.GetLiveStreamsByCategoryAsync(
          plugin.Creds,
          categoryId,
          cancellationToken).ConfigureAwait(false);
        return Ok(streams.Select(CreateItemResponse));
    }

    /// <summary>
    /// Get all VOD categories.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token for cancelling requests.</param>
    /// <returns>An enumerable containing the categories.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("VodCategories")]
    public async Task<ActionResult<IEnumerable<CategoryResponse>>> GetVodCategories(CancellationToken cancellationToken)
    {
        Plugin plugin = Plugin.Instance;
        List<Category> categories = await xtreamClient.GetVodCategoryAsync(plugin.Creds, cancellationToken).ConfigureAwait(false);
        return Ok(categories.Select(CreateCategoryResponse));
    }

    /// <summary>
    /// Get all VOD streams for the given category.
    /// </summary>
    /// <param name="categoryId">The category for which to fetch the streams.</param>
    /// <param name="cancellationToken">The cancellation token for cancelling requests.</param>
    /// <returns>An enumerable containing the streams.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("VodCategories/{categoryId}")]
    public async Task<ActionResult<IEnumerable<StreamInfo>>> GetVodStreams(int categoryId, CancellationToken cancellationToken)
    {
        Plugin plugin = Plugin.Instance;
        List<StreamInfo> streams = await xtreamClient.GetVodStreamsByCategoryAsync(
          plugin.Creds,
          categoryId,
          cancellationToken).ConfigureAwait(false);
        return Ok(streams.Select(CreateItemResponse));
    }

    /// <summary>
    /// Get all Series categories.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token for cancelling requests.</param>
    /// <returns>An enumerable containing the categories.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("SeriesCategories")]
    public async Task<ActionResult<IEnumerable<CategoryResponse>>> GetSeriesCategories(CancellationToken cancellationToken)
    {
        Plugin plugin = Plugin.Instance;
        List<Category> categories = await xtreamClient.GetSeriesCategoryAsync(plugin.Creds, cancellationToken).ConfigureAwait(false);
        return Ok(categories.Select(CreateCategoryResponse));
    }

    /// <summary>
    /// Get all Series streams for the given category.
    /// </summary>
    /// <param name="categoryId">The category for which to fetch the streams.</param>
    /// <param name="cancellationToken">The cancellation token for cancelling requests.</param>
    /// <returns>An enumerable containing the streams.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("SeriesCategories/{categoryId}")]
    public async Task<ActionResult<IEnumerable<StreamInfo>>> GetSeriesStreams(int categoryId, CancellationToken cancellationToken)
    {
        Plugin plugin = Plugin.Instance;
        List<Series> series = await xtreamClient.GetSeriesByCategoryAsync(
          plugin.Creds,
          categoryId,
          cancellationToken).ConfigureAwait(false);
        return Ok(series.Select(CreateItemResponse));
    }

    /// <summary>
    /// Get the TV channels currently matched by each of the given dynamic keyword rules.
    /// The rules do not need to be saved, so this can be used to preview unsaved edits.
    /// </summary>
    /// <param name="rules">The rules to evaluate.</param>
    /// <param name="cancellationToken">The cancellation token for cancelling requests.</param>
    /// <returns>For every rule, in the same order, the channels it matches.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpPost("LiveTv/DynamicMatches")]
    public async Task<ActionResult<IEnumerable<IEnumerable<ChannelResponse>>>> GetDynamicMatches(
        [FromBody] IReadOnlyList<DynamicChannelRule> rules,
        CancellationToken cancellationToken)
    {
        if (rules.Count == 0)
        {
            return Ok(Array.Empty<ChannelResponse[]>());
        }

        foreach (DynamicChannelRule rule in rules)
        {
            // Preview regardless of the enabled flag.
            rule.Enabled = true;
        }

        List<StreamInfo> streams = await xtreamClient.GetLiveStreamsAsync(Plugin.Instance.Creds, cancellationToken).ConfigureAwait(false);
        DateTime now = DateTime.UtcNow;
        List<List<ChannelResponse>> result = [];
        foreach (DynamicChannelRule rule in rules)
        {
            TeamSchedule? schedule = rule.HasTeam
                ? await sportsService.GetScheduleAsync(rule.League, rule.TeamId, cancellationToken).ConfigureAwait(false)
                : null;
            result.Add(streams.Where(s => rule.Matches(s.CategoryId, s.Name)).Select(s =>
            {
                ChannelResponse response = CreateChannelResponse(s);
                if (schedule is not null)
                {
                    SportsGame? game = SportsService.MatchGame(schedule, s.Name, now);
                    response.Game = game is null
                        ? null
                        : $"{game.ShortName} · {SportsService.FormatStart(game, includeDay: true)}";
                    response.ListedToday = game is null || SportsService.IsGameDay(game, now);
                }

                return response;
            }).ToList());
        }

        return Ok(result);
    }

    /// <summary>
    /// Get the next planned smart refresh of the TV guide.
    /// </summary>
    /// <returns>The next planned run, or no content when none is planned.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("Sports/SmartRefresh")]
    public ActionResult<PlannedRun> GetSmartRefresh()
    {
        return smartRefreshService.NextRun is PlannedRun next ? Ok(next) : NoContent();
    }

    /// <summary>
    /// Get the leagues for which team schedules can be looked up.
    /// </summary>
    /// <returns>The leagues.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("Sports/Leagues")]
    public ActionResult<IEnumerable<SportsLeague>> GetSportsLeagues()
    {
        return Ok(SportsService.Leagues);
    }

    /// <summary>
    /// Get the teams of a league.
    /// </summary>
    /// <param name="league">The league id, e.g. <c>basketball/nba</c>.</param>
    /// <param name="cancellationToken">The cancellation token for cancelling requests.</param>
    /// <returns>The teams.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("Sports/Teams")]
    public async Task<ActionResult<IEnumerable<SportsTeam>>> GetSportsTeams([FromQuery] string league, CancellationToken cancellationToken)
    {
        if (!SportsService.IsSupportedLeague(league))
        {
            return BadRequest("Unsupported league");
        }

        return Ok(await sportsService.GetTeamsAsync(league, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Get the upcoming games of a team.
    /// </summary>
    /// <param name="league">The league id, e.g. <c>basketball/nba</c>.</param>
    /// <param name="teamId">The team id.</param>
    /// <param name="cancellationToken">The cancellation token for cancelling requests.</param>
    /// <returns>The next few games, formatted for display.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("Sports/Schedule")]
    public async Task<ActionResult<IEnumerable<string>>> GetSportsSchedule([FromQuery] string league, [FromQuery] string teamId, CancellationToken cancellationToken)
    {
        TeamSchedule? schedule = await sportsService.GetScheduleAsync(league, teamId, cancellationToken).ConfigureAwait(false);
        if (schedule is null)
        {
            return NotFound();
        }

        DateTime now = DateTime.UtcNow;
        return Ok(schedule.Games
            .Where(g => g.EndUtc >= now)
            .Take(3)
            .Select(g => $"{g.Name} · {SportsService.FormatStart(g, includeDay: true)}"));
    }

    /// <summary>
    /// Get all configured TV channels.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token for cancelling requests.</param>
    /// <returns>An enumerable containing the streams.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("LiveTv")]
    public async Task<ActionResult<IEnumerable<StreamInfo>>> GetLiveTvChannels(CancellationToken cancellationToken)
    {
        IEnumerable<StreamInfo> streams = await Plugin.Instance.StreamService.GetLiveStreams(cancellationToken).ConfigureAwait(false);
        var channels = streams.Select(CreateChannelResponse).ToList();
        return Ok(channels);
    }
}
