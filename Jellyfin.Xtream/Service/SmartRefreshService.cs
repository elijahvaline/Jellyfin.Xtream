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
using Jellyfin.Xtream.Configuration;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// Refreshes the TV guide around the games of teams linked to dynamic rules, on top of its regular schedule:
/// shortly before each game (so its streams, guide entry and favorites are in place) and some hours after
/// it started (to clean up). Every time the guide refresh completes, the next run is planned again.
/// </summary>
/// <param name="taskManager">Instance of the <see cref="ITaskManager"/> interface.</param>
/// <param name="sportsService">Instance of the <see cref="SportsService"/> class.</param>
/// <param name="logger">Instance of the <see cref="ILogger"/> interface.</param>
public sealed class SmartRefreshService(
    ITaskManager taskManager,
    SportsService sportsService,
    ILogger<SmartRefreshService> logger) : IHostedService, IDisposable
{
    private const string RefreshGuideTask = "Jellyfin.LiveTv.Guide.RefreshGuideScheduledTask";

    /// <summary>
    /// How often the plan is recomputed even without a run, to pick up schedule changes.
    /// </summary>
    private static readonly TimeSpan _recheckInterval = TimeSpan.FromHours(3);

    private readonly SemaphoreSlim _planLock = new(1, 1);
    private Timer? _timer;
    private PlannedRun? _next;

    /// <summary>
    /// Gets the next planned smart refresh, or <c>null</c> when none is planned.
    /// </summary>
    public PlannedRun? NextRun => _next;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timer = new Timer(OnTimer);
        taskManager.TaskCompleted += OnTaskCompleted;

        // Plan once the server has settled.
        _timer.Change(TimeSpan.FromMinutes(1), Timeout.InfiniteTimeSpan);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        taskManager.TaskCompleted -= OnTaskCompleted;
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _timer?.Dispose();
        _planLock.Dispose();
    }

    /// <summary>
    /// Plans the next smart refresh from the schedules of all linked teams.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public async Task PlanAsync()
    {
        await _planLock.WaitAsync().ConfigureAwait(false);
        try
        {
            PluginConfiguration config = Plugin.Instance.Configuration;
            DateTime now = DateTime.UtcNow;
            PlannedRun? next = null;

            if (config.SmartRefreshEnabled)
            {
                TimeSpan before = TimeSpan.FromMinutes(Math.Max(0, config.SmartRefreshBeforeMinutes));
                TimeSpan after = TimeSpan.FromMinutes(Math.Max(0, config.SmartRefreshAfterMinutes));
                IEnumerable<DynamicChannelRule> rules = config.LiveTvDynamic
                    .Where(r => r.Enabled && r.HasTeam && !string.IsNullOrWhiteSpace(r.Keyword))
                    .DistinctBy(r => (r.League, r.TeamId));

                foreach (DynamicChannelRule rule in rules)
                {
                    TeamSchedule? schedule = await sportsService.GetScheduleAsync(rule.League, rule.TeamId, CancellationToken.None).ConfigureAwait(false);
                    foreach (SportsGame game in schedule?.Games.Where(g => g.TimeValid) ?? [])
                    {
                        // Only plan runs at least a minute away, so a run never immediately plans itself again.
                        foreach (PlannedRun candidate in new[]
                        {
                            new PlannedRun(game.StartUtc - before, $"{FormatOffset(before)} before {game.Name}"),
                            new PlannedRun(game.StartUtc + after, $"{FormatOffset(after)} after the start of {game.Name}"),
                        })
                        {
                            if (candidate.TimeUtc > now.AddMinutes(1) && (next is null || candidate.TimeUtc < next.TimeUtc))
                            {
                                next = candidate;
                            }
                        }
                    }
                }
            }

            _next = next;
            TimeSpan wait = next is null || next.TimeUtc - now > _recheckInterval ? _recheckInterval : next.TimeUtc - now;
            _timer?.Change(wait, Timeout.InfiniteTimeSpan);
            if (next is not null)
            {
                logger.LogInformation("Smart refresh: next guide refresh at {Time:u} ({Reason})", next.TimeUtc, next.Reason);
            }
        }
        finally
        {
            _planLock.Release();
        }
    }

    private static string FormatOffset(TimeSpan offset) =>
        offset.TotalMinutes % 60 == 0 ? $"{offset.TotalHours:0} h" : $"{offset.TotalMinutes:0} min";

    private void OnTaskCompleted(object? sender, TaskCompletionEventArgs e)
    {
        if (e.Task.ScheduledTask.GetType().FullName == RefreshGuideTask)
        {
            _ = PlanSafeAsync();
        }
    }

    private void OnTimer(object? state)
    {
        PlannedRun? due = _next;
        if (due is not null && due.TimeUtc <= DateTime.UtcNow.AddSeconds(30))
        {
            _next = null;
            RunGuideRefresh(due);
        }

        // Plan again in any case; when a refresh was queued, its completion plans again as well.
        _ = PlanSafeAsync();
    }

    private void RunGuideRefresh(PlannedRun run)
    {
        try
        {
            IScheduledTaskWorker? worker = taskManager.ScheduledTasks
                .FirstOrDefault(t => t.ScheduledTask.GetType().FullName == RefreshGuideTask);
            if (worker is null)
            {
                logger.LogWarning("Smart refresh: the guide refresh task was not found");
                return;
            }

            // Make sure the refresh sees the provider's current streams.
            sportsService.InvalidateMatches();
            if (worker.State == TaskState.Idle)
            {
                logger.LogInformation("Smart refresh: refreshing the guide ({Reason})", run.Reason);
                taskManager.QueueScheduledTask(worker.ScheduledTask, new TaskOptions());
            }
            else
            {
                logger.LogInformation("Smart refresh: guide refresh already running ({Reason})", run.Reason);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Smart refresh: failed to queue the guide refresh");
        }
    }

    private async Task PlanSafeAsync()
    {
        try
        {
            await PlanAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Smart refresh: failed to plan the next guide refresh");
            _timer?.Change(TimeSpan.FromMinutes(15), Timeout.InfiniteTimeSpan);
        }
    }
}
