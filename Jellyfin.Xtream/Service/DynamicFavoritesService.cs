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
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Xtream.Client;
using Jellyfin.Xtream.Client.Models;
using Jellyfin.Xtream.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// Marks channels matched by dynamic rules as favorites for every user after each guide refresh.
/// A channel is favorited once when it starts matching (so a user can still unfavorite it manually),
/// and unfavorited again when it stops matching while still being present in Live TV.
/// </summary>
/// <param name="taskManager">Instance of the <see cref="ITaskManager"/> interface.</param>
/// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
/// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
/// <param name="userDataManager">Instance of the <see cref="IUserDataManager"/> interface.</param>
/// <param name="xtreamClient">Instance of the <see cref="IXtreamClient"/> interface.</param>
/// <param name="logger">Instance of the <see cref="ILogger"/> interface.</param>
public class DynamicFavoritesService(
    ITaskManager taskManager,
    ILibraryManager libraryManager,
    IUserManager userManager,
    IUserDataManager userDataManager,
    IXtreamClient xtreamClient,
    ILogger<DynamicFavoritesService> logger) : IHostedService
{
    private const string RefreshGuideTask = "Jellyfin.LiveTv.Guide.RefreshGuideScheduledTask";

    private const string StateFile = "dynamic-favorites.json";

    private int _running;

    private static string StatePath => Path.Combine(Plugin.Instance.DataFolderPath, StateFile);

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        taskManager.TaskCompleted += OnTaskCompleted;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        taskManager.TaskCompleted -= OnTaskCompleted;
        return Task.CompletedTask;
    }

    private static HashSet<string> LoadState()
    {
        try
        {
            if (File.Exists(StatePath))
            {
                return JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(StatePath)) ?? [];
            }
        }
        catch (Exception)
        {
            // Corrupt state: start fresh.
        }

        return [];
    }

    private static void SaveState(HashSet<string> state)
    {
        Directory.CreateDirectory(Plugin.Instance.DataFolderPath);
        File.WriteAllText(StatePath, JsonSerializer.Serialize(state));
    }

    private void OnTaskCompleted(object? sender, TaskCompletionEventArgs e)
    {
        if (e.Result.Status != TaskCompletionStatus.Completed
            || e.Task.ScheduledTask.GetType().FullName != RefreshGuideTask)
        {
            return;
        }

        _ = Task.Run(SyncSafeAsync);
    }

    private async Task SyncSafeAsync()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return;
        }

        try
        {
            await SyncAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update favorites for dynamic channels");
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private async Task SyncAsync(CancellationToken cancellationToken)
    {
        Plugin plugin = Plugin.Instance;
        List<DynamicChannelRule> rules = plugin.Configuration.LiveTvDynamic
            .Where(r => r.Enabled && r.Favorite && !string.IsNullOrWhiteSpace(r.Keyword))
            .ToList();
        HashSet<string> state = LoadState();
        if (rules.Count == 0 && state.Count == 0)
        {
            return;
        }

        // Channel ids (as reported to Jellyfin) that should currently be favorites.
        HashSet<string> wanted = [];
        if (rules.Count > 0)
        {
            List<StreamInfo> streams = await xtreamClient.GetLiveStreamsAsync(plugin.Creds, cancellationToken).ConfigureAwait(false);
            foreach (StreamInfo stream in streams.Where(s => rules.Any(r => r.Matches(s.CategoryId, s.Name))))
            {
                wanted.Add(StreamService.ToGuid(StreamService.LiveTvPrefix, stream.StreamId, 0, 0).ToString());
            }
        }

        Dictionary<string, BaseItem> channels = libraryManager
            .GetItemList(new InternalItemsQuery { IncludeItemTypes = [BaseItemKind.LiveTvChannel] })
            .Where(i => i.ServiceName == LiveTvService.ServiceName && !string.IsNullOrEmpty(i.ExternalId))
            .GroupBy(i => i.ExternalId)
            .ToDictionary(g => g.Key, g => g.First());

        List<User> users = userManager.GetUsers().ToList();
        int added = 0;
        int removed = 0;

        foreach (string id in wanted.Except(state).ToList())
        {
            if (channels.TryGetValue(id, out BaseItem? item))
            {
                SetFavorite(users, item, true, cancellationToken);
                state.Add(id);
                added++;
            }
        }

        foreach (string id in state.Except(wanted).ToList())
        {
            if (channels.TryGetValue(id, out BaseItem? item))
            {
                SetFavorite(users, item, false, cancellationToken);
                removed++;
            }

            state.Remove(id);
        }

        SaveState(state);
        if (added > 0 || removed > 0)
        {
            logger.LogInformation("Dynamic channels: favorited {Added}, unfavorited {Removed} channels", added, removed);
        }
    }

    private void SetFavorite(List<User> users, BaseItem item, bool favorite, CancellationToken cancellationToken)
    {
        foreach (User user in users)
        {
            UserItemData? data = userDataManager.GetUserData(user, item);
            if (data is null || data.IsFavorite == favorite)
            {
                continue;
            }

            data.IsFavorite = favorite;
            userDataManager.SaveUserData(user, item, data, UserDataSaveReason.UpdateUserRating, cancellationToken);
        }
    }
}
