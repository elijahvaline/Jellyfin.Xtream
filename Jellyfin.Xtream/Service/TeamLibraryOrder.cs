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
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// Moves the team libraries to the front of every user's library order. Jellyfin puts libraries that are not in a
/// user's saved order at the end, and team libraries are only visible on game days, so they can't reliably be
/// ordered by hand. The rest of each user's saved order is kept as it is.
/// </summary>
/// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
/// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
/// <param name="logger">Instance of the <see cref="ILogger"/> interface.</param>
public class TeamLibraryOrder(IUserManager userManager, ILibraryManager libraryManager, ILogger<TeamLibraryOrder> logger)
{
    /// <summary>
    /// Puts the team libraries first in every user's library order, if enabled.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public async Task ApplyAsync()
    {
        if (!Plugin.Instance.Configuration.TeamLibrariesFirst)
        {
            return;
        }

        // Jellyfin derives a channel's id from its name (see ChannelManager.GetInternalChannelId),
        // so the ids are known even while a team library is hidden.
        Guid[] teamIds = TeamSlotChannel.GetTeamRules()
            .Select(t => libraryManager.GetNewItemId("Channel " + t.Name, typeof(Channel)))
            .ToArray();
        if (teamIds.Length == 0)
        {
            return;
        }

        int updated = 0;
        foreach (User user in userManager.GetUsers().ToList())
        {
            Guid[] current = user.GetPreferenceValues<Guid>(PreferenceKind.OrderedViews);
            Guid[] wanted = [.. teamIds, .. current.Where(id => !teamIds.Contains(id))];
            if (current.SequenceEqual(wanted))
            {
                continue;
            }

            user.SetPreference(PreferenceKind.OrderedViews, wanted);
            await userManager.UpdateUserAsync(user).ConfigureAwait(false);
            updated++;
        }

        if (updated > 0)
        {
            logger.LogInformation("Moved the team libraries to the front for {Count} users", updated);
        }
    }
}
