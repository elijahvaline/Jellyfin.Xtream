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
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Client;
using Jellyfin.Xtream.Client.Models;
using Jellyfin.Xtream.Configuration;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// Looks up team schedules from ESPN's public site API and matches them to streams.
/// Every lookup degrades gracefully: when ESPN is unreachable, streams are simply treated as unscheduled.
/// </summary>
/// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface.</param>
/// <param name="memoryCache">Instance of the <see cref="IMemoryCache"/> interface.</param>
/// <param name="xtreamClient">Instance of the <see cref="IXtreamClient"/> interface.</param>
/// <param name="logger">Instance of the <see cref="ILogger"/> interface.</param>
public partial class SportsService(
    IHttpClientFactory httpClientFactory,
    IMemoryCache memoryCache,
    IXtreamClient xtreamClient,
    ILogger<SportsService> logger)
{
    private const string BaseUrl = "https://site.api.espn.com/apis/site/v2/sports/";

    private static readonly TimeSpan _matchesLifetime = TimeSpan.FromMinutes(5);

    private readonly object _snapshotLock = new();
    private Snapshot? _snapshot;
    private int _refreshing;
    private int _generation;

    /// <summary>
    /// Gets the supported leagues.
    /// </summary>
    public static IReadOnlyList<SportsLeague> Leagues { get; } =
    [
        new("football/nfl", "NFL"),
        new("basketball/nba", "NBA"),
        new("basketball/wnba", "WNBA"),
        new("hockey/nhl", "NHL"),
        new("baseball/mlb", "MLB"),
        new("soccer/usa.1", "MLS"),
        new("soccer/eng.1", "Premier League"),
        new("football/college-football", "College Football"),
        new("basketball/mens-college-basketball", "Men's College Basketball"),
        new("basketball/womens-college-basketball", "Women's College Basketball"),
        new("hockey/mens-college-hockey", "Men's College Hockey"),
    ];

    /// <summary>
    /// Gets the time zone used to decide which day a game is on and to display game times.
    /// </summary>
    public static TimeZoneInfo TimeZone
    {
        get
        {
            string id = Plugin.Instance.Configuration.SportsTimeZone;
            return !string.IsNullOrWhiteSpace(id) && TimeZoneInfo.TryFindSystemTimeZoneById(id.Trim(), out TimeZoneInfo? zone)
                ? zone
                : TimeZoneInfo.Local;
        }
    }

    /// <summary>
    /// Checks whether the league id is supported.
    /// </summary>
    /// <param name="league">The league id.</param>
    /// <returns>Whether the league is supported.</returns>
    public static bool IsSupportedLeague(string? league) => Leagues.Any(l => l.Id == league);

    /// <summary>
    /// Checks whether today (in <see cref="TimeZone"/>) is the day of the game, or the game is still running.
    /// </summary>
    /// <param name="game">The game.</param>
    /// <param name="nowUtc">The current time in UTC.</param>
    /// <returns>Whether streams for this game should be listed.</returns>
    public static bool IsGameDay(SportsGame game, DateTime nowUtc)
    {
        TimeZoneInfo zone = TimeZone;
        DateTime localStart = TimeZoneInfo.ConvertTimeFromUtc(game.StartUtc, zone);
        DateTime dayStartUtc = TimeZoneInfo.ConvertTimeToUtc(localStart.Date, zone);
        DateTime visibleUntil = game.TimeValid
            ? game.EndUtc.AddHours(1)
            : TimeZoneInfo.ConvertTimeToUtc(localStart.Date.AddDays(1), zone);
        return nowUtc >= dayStartUtc && nowUtc <= visibleUntil;
    }

    /// <summary>
    /// Formats the start of the game in <see cref="TimeZone"/>, e.g. <c>7:00 PM</c> or <c>Sun 7:00 PM</c>.
    /// </summary>
    /// <param name="game">The game.</param>
    /// <param name="includeDay">Whether to include the day of the week.</param>
    /// <returns>The formatted start time.</returns>
    public static string FormatStart(SportsGame game, bool includeDay)
    {
        DateTime local = TimeZoneInfo.ConvertTimeFromUtc(game.StartUtc, TimeZone);
        string time = game.TimeValid ? local.ToString("h:mm tt", CultureInfo.InvariantCulture) : "TBD";
        return includeDay ? $"{local.ToString("ddd MMM d", CultureInfo.InvariantCulture)}, {time}" : time;
    }

    /// <summary>
    /// Gets the teams of a league.
    /// </summary>
    /// <param name="league">The league id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The teams, ordered by name.</returns>
    public async Task<IReadOnlyList<SportsTeam>> GetTeamsAsync(string league, CancellationToken cancellationToken)
    {
        if (!IsSupportedLeague(league))
        {
            throw new ArgumentException("Unsupported league", nameof(league));
        }

        string key = $"xtream-sports-teams-{league}";
        if (memoryCache.TryGetValue(key, out IReadOnlyList<SportsTeam>? cached) && cached is not null)
        {
            return cached;
        }

        using JsonDocument doc = await FetchAsync($"{league}/teams?limit=1000", cancellationToken).ConfigureAwait(false);
        List<SportsTeam> teams = [];
        foreach (JsonElement league0 in Array(doc.RootElement, "sports").SelectMany(s => Array(s, "leagues")))
        {
            foreach (JsonElement entry in Array(league0, "teams"))
            {
                if (entry.TryGetProperty("team", out JsonElement team) && ParseTeam(team) is SportsTeam parsed)
                {
                    teams.Add(parsed);
                }
            }
        }

        teams.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        memoryCache.Set(key, (IReadOnlyList<SportsTeam>)teams, DateTimeOffset.Now.AddHours(12));
        return teams;
    }

    /// <summary>
    /// Gets the schedule of a team. Failures are logged and cached briefly, and return <c>null</c>.
    /// </summary>
    /// <param name="league">The league id.</param>
    /// <param name="teamId">The team id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The schedule, or <c>null</c> if it could not be loaded.</returns>
    public async Task<TeamSchedule?> GetScheduleAsync(string league, string teamId, CancellationToken cancellationToken)
    {
        if (!IsSupportedLeague(league) || !TeamIdRegex().IsMatch(teamId))
        {
            return null;
        }

        string key = $"xtream-sports-schedule-{league}-{teamId}";
        if (memoryCache.TryGetValue(key, out TeamSchedule? cached))
        {
            return cached;
        }

        TeamSchedule? schedule = null;
        try
        {
            schedule = await LoadScheduleAsync(league, teamId, string.Empty, cancellationToken).ConfigureAwait(false);
            if (schedule is not null && !schedule.Games.Any(g => g.EndUtc >= DateTime.UtcNow))
            {
                // Nothing left in the current season type, e.g. the regular season ended: try the postseason.
                TeamSchedule? post = await LoadScheduleAsync(league, teamId, "?seasontype=3", cancellationToken).ConfigureAwait(false);
                if (post is not null)
                {
                    schedule = schedule with
                    {
                        Games = [.. schedule.Games.Concat(post.Games).DistinctBy(g => g.Id).OrderBy(g => g.StartUtc)],
                    };
                }
            }

            memoryCache.Set(key, schedule, DateTimeOffset.Now.AddHours(3));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Failed to load the {League} schedule for team {TeamId} from ESPN", league, teamId);
            memoryCache.Set(key, (TeamSchedule?)null, DateTimeOffset.Now.AddMinutes(15));
        }

        return schedule;
    }

    /// <summary>
    /// Finds the game a stream shows: the stream name must contain both the team's and an opponent's name.
    /// When the same opponent is played more than once, the next game that has not ended yet is picked.
    /// </summary>
    /// <param name="schedule">The schedule of the team.</param>
    /// <param name="streamName">The original stream name.</param>
    /// <param name="nowUtc">The current time in UTC.</param>
    /// <returns>The matched game, or <c>null</c>.</returns>
    public static SportsGame? MatchGame(TeamSchedule schedule, string streamName, DateTime nowUtc)
    {
        if (!schedule.TeamNames.Any(name => ContainsWord(streamName, name)))
        {
            return null;
        }

        List<SportsGame> candidates = schedule.Games
            .Where(g => g.OpponentNames.Any(name => ContainsWord(streamName, name)))
            .ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        return candidates.Where(g => g.EndUtc.AddHours(1) >= nowUtc).OrderBy(g => g.StartUtc).FirstOrDefault()
            ?? candidates.OrderByDescending(g => g.StartUtc).First();
    }

    /// <summary>
    /// Gets, for every enabled dynamic rule which shows a folder or is linked to a team,
    /// the streams it matches and the game each stream shows. Cached for a few minutes.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The matches per rule.</returns>
    public async Task<IReadOnlyList<RuleMatches>> GetRuleMatchesAsync(CancellationToken cancellationToken)
    {
        Plugin plugin = Plugin.Instance;
        PluginConfiguration config = plugin.Configuration;
        string key = $"xtream-sports-rules-{plugin.DataVersion}-{Volatile.Read(ref _generation)}";
        if (memoryCache.TryGetValue(key, out IReadOnlyList<RuleMatches>? cached) && cached is not null)
        {
            return cached;
        }

        List<DynamicChannelRule> rules = config.LiveTvDynamic
            .Where(r => r.Enabled && !string.IsNullOrWhiteSpace(r.Keyword) && (r.ShowAsFolder || r.HasTeam))
            .ToList();
        List<RuleMatches> result = [];
        if (rules.Count > 0)
        {
            List<StreamInfo> streams = await xtreamClient.GetLiveStreamsAsync(plugin.Creds, cancellationToken).ConfigureAwait(false);
            HashSet<int> hidden = [.. config.LiveTvDynamicHidden];
            DateTime now = DateTime.UtcNow;
            foreach (DynamicChannelRule rule in rules)
            {
                TeamSchedule? schedule = rule.HasTeam
                    ? await GetScheduleAsync(rule.League, rule.TeamId, cancellationToken).ConfigureAwait(false)
                    : null;
                List<MatchedChannel> matched = streams
                    .Where(s => StreamService.IsDynamicMatch([rule], hidden, s))
                    .Select(s => new MatchedChannel(s, schedule is null ? null : MatchGame(schedule, s.Name, now)))
                    .ToList();
                result.Add(new RuleMatches(rule, schedule, matched));
            }
        }

        memoryCache.Set(key, (IReadOnlyList<RuleMatches>)result, DateTimeOffset.Now.Add(_matchesLifetime));
        lock (_snapshotLock)
        {
            _snapshot = new Snapshot(plugin.DataVersion, DateTime.UtcNow, result);
        }

        return result;
    }

    /// <summary>
    /// Forgets the cached rule matches, so the next lookup fetches the current streams from the provider.
    /// </summary>
    public void InvalidateMatches()
    {
        Interlocked.Increment(ref _generation);
        lock (_snapshotLock)
        {
            _snapshot = null;
        }
    }

    /// <summary>
    /// Gets the most recent rule matches without waiting, for callers that must answer synchronously
    /// (e.g. deciding which libraries a user sees). A stale snapshot is refreshed in the background;
    /// only the very first call, or the first call after a configuration change, waits for the result.
    /// </summary>
    /// <returns>The matches per rule.</returns>
    public IReadOnlyList<RuleMatches> GetRuleMatchesSnapshot()
    {
        Snapshot? snapshot;
        lock (_snapshotLock)
        {
            snapshot = _snapshot;
        }

        if (snapshot is null || snapshot.Version != Plugin.Instance.DataVersion)
        {
            try
            {
                return GetRuleMatchesAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to match streams of team rules");
                return snapshot?.Matches ?? [];
            }
        }

        if (DateTime.UtcNow - snapshot.CreatedUtc > _matchesLifetime && Interlocked.Exchange(ref _refreshing, 1) == 0)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await GetRuleMatchesAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to refresh the matched streams of team rules");
                }
                finally
                {
                    Interlocked.Exchange(ref _refreshing, 0);
                }
            });
        }

        return snapshot.Matches;
    }

    private static TimeSpan GameDuration(string league) => league.Split('/')[0] switch
    {
        "football" => TimeSpan.FromHours(3.5),
        "basketball" => TimeSpan.FromHours(2.5),
        "hockey" => TimeSpan.FromHours(2.75),
        "baseball" => TimeSpan.FromHours(3),
        "soccer" => TimeSpan.FromHours(2),
        _ => TimeSpan.FromHours(3),
    };

    private static bool ContainsWord(string text, string word)
    {
        int at = 0;
        while ((at = text.IndexOf(word, at, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            int end = at + word.Length;
            bool startOk = at == 0 || !char.IsLetterOrDigit(text[at - 1]);
            bool endOk = end == text.Length || !char.IsLetterOrDigit(text[end]);
            if (startOk && endOk)
            {
                return true;
            }

            at++;
        }

        return false;
    }

    private static JsonElement[] Array(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(property, out JsonElement value)
            && value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray()]
            : [];

    private static string? Str(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(property, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static SportsTeam? ParseTeam(JsonElement team)
    {
        string? id = Str(team, "id");
        string? name = Str(team, "displayName");
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name))
        {
            return null;
        }

        string? logo = Str(team, "logo") ?? Array(team, "logos").Select(l => Str(l, "href")).FirstOrDefault(h => h is not null);
        return new SportsTeam(id, name, Str(team, "abbreviation") ?? string.Empty, logo);
    }

    private async Task<JsonDocument> FetchAsync(string path, CancellationToken cancellationToken)
    {
        HttpClient client = httpClientFactory.CreateClient(NamedClient.Default);
        using Stream stream = await client.GetStreamAsync(new Uri(BaseUrl + path), cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task<TeamSchedule?> LoadScheduleAsync(string league, string teamId, string query, CancellationToken cancellationToken)
    {
        using JsonDocument doc = await FetchAsync($"{league}/teams/{teamId}/schedule{query}", cancellationToken).ConfigureAwait(false);
        JsonElement root = doc.RootElement;
        if (!root.TryGetProperty("team", out JsonElement teamElement) || ParseTeam(teamElement) is not SportsTeam team)
        {
            return null;
        }

        TimeSpan duration = GameDuration(league);
        List<SportsGame> games = [];
        foreach (JsonElement ev in Array(root, "events"))
        {
            JsonElement competition = Array(ev, "competitions").FirstOrDefault();
            string? id = Str(ev, "id");
            string? date = Str(competition, "date") ?? Str(ev, "date");
            if (id is null || date is null
                || !DateTime.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime start))
            {
                continue;
            }

            List<string> opponentNames = [];
            foreach (JsonElement competitor in Array(competition, "competitors"))
            {
                if (competitor.TryGetProperty("team", out JsonElement t) && Str(t, "id") != team.Id)
                {
                    AddNames(opponentNames, t);
                }
            }

            bool timeValid = !(competition.ValueKind == JsonValueKind.Object
                && competition.TryGetProperty("timeValid", out JsonElement tv)
                && tv.ValueKind == JsonValueKind.False);
            string broadcast = string.Join(", ", Array(competition, "broadcasts")
                .Select(b => b.TryGetProperty("media", out JsonElement m) ? Str(m, "shortName") : null)
                .Where(n => !string.IsNullOrEmpty(n))
                .Distinct());

            games.Add(new SportsGame(
                id,
                start,
                timeValid,
                start + duration,
                Str(ev, "name") ?? string.Empty,
                Str(ev, "shortName") ?? string.Empty,
                broadcast,
                opponentNames));
        }

        games.Sort((a, b) => a.StartUtc.CompareTo(b.StartUtc));
        List<string> teamNames = [];
        AddNames(teamNames, teamElement);
        return new TeamSchedule(team, teamNames, games);
    }

    private static void AddNames(List<string> names, JsonElement team)
    {
        foreach (string? name in new[] { Str(team, "displayName"), Str(team, "shortDisplayName"), Str(team, "nickname"), Str(team, "name") })
        {
            if (name is { Length: >= 3 } && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                names.Add(name);
            }
        }
    }

    [GeneratedRegex("^[A-Za-z0-9-]{1,20}$")]
    private static partial Regex TeamIdRegex();

    private sealed record Snapshot(string Version, DateTime CreatedUtc, IReadOnlyList<RuleMatches> Matches);
}
