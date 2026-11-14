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
using System.Collections.ObjectModel;

#pragma warning disable CA2227
namespace Jellyfin.Xtream.Configuration;

/// <summary>
/// A rule which dynamically includes Live TV channels whose name contains a keyword.
/// Rules are evaluated on every guide refresh, so channels are added when they
/// start matching and removed again once they no longer match.
/// </summary>
public class DynamicChannelRule
{
    /// <summary>
    /// Gets or sets the keyword which must be contained in the channel name (case-insensitive).
    /// </summary>
    public string Keyword { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether this rule is active.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether matched channels are marked as favorite for all users.
    /// </summary>
    public bool Favorite { get; set; } = true;

    /// <summary>
    /// Gets or sets the category ids in which to search. An empty collection searches all categories.
    /// </summary>
    public Collection<int> Categories { get; set; } = [];

    /// <summary>
    /// Checks whether the given channel matches this rule.
    /// </summary>
    /// <param name="categoryId">The category of the channel.</param>
    /// <param name="name">The original name of the channel.</param>
    /// <returns>Whether the channel matches.</returns>
    public bool Matches(int? categoryId, string? name)
    {
        string keyword = Keyword.Trim();
        if (!Enabled || keyword.Length == 0 || string.IsNullOrEmpty(name))
        {
            return false;
        }

        if (Categories.Count > 0 && (!categoryId.HasValue || !Categories.Contains(categoryId.Value)))
        {
            return false;
        }

        return name.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }
}
#pragma warning restore CA2227
