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
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;

#pragma warning disable CA2227
namespace Jellyfin.Xtream.Configuration;

/// <summary>
/// A rule which dynamically includes Live TV channels whose name contains all of its keywords.
/// Rules are evaluated on every guide refresh, so channels are added when they
/// start matching and removed again once they no longer match.
/// </summary>
public partial class DynamicChannelRule
{
    private ParsedKeyword _parsed = new(string.Empty, []);

    /// <summary>
    /// Gets or sets the keywords which must all be contained in the channel name (case-insensitive, any order).
    /// Words are separated by spaces; use double quotes to keep a phrase together, e.g. <c>"fox sports" wisconsin</c>.
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
    /// Gets or sets a value indicating whether matched channels are also listed in a folder in the Xtream Teams channel.
    /// </summary>
    public bool ShowAsFolder { get; set; }

    /// <summary>
    /// Gets or sets the folder name. When empty, the linked team's name or the keywords are used.
    /// </summary>
    public string FolderName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the ESPN league path of the linked team, e.g. <c>basketball/nba</c>. Empty when no team is linked.
    /// </summary>
    public string League { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the ESPN id of the linked team. Empty when no team is linked.
    /// </summary>
    public string TeamId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the display name of the linked team, kept so it can be shown without querying ESPN.
    /// </summary>
    public string TeamName { get; set; } = string.Empty;

    /// <summary>
    /// Gets a value indicating whether this rule is linked to a team schedule.
    /// </summary>
    [System.Xml.Serialization.XmlIgnore]
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasTeam => !string.IsNullOrWhiteSpace(League) && !string.IsNullOrWhiteSpace(TeamId);

    /// <summary>
    /// Gets the display name of the folder for this rule.
    /// </summary>
    [System.Xml.Serialization.XmlIgnore]
    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayFolderName =>
        !string.IsNullOrWhiteSpace(FolderName) ? FolderName.Trim()
        : HasTeam && !string.IsNullOrWhiteSpace(TeamName) ? TeamName
        : Keyword.Trim();

    /// <summary>
    /// Gets a stable identifier for this rule's folder, derived from what the rule matches.
    /// </summary>
    [System.Xml.Serialization.XmlIgnore]
    [System.Text.Json.Serialization.JsonIgnore]
    public int FolderKey => StableHash($"{Keyword.Trim().ToUpperInvariant()}|{League}|{TeamId}");

    /// <summary>
    /// Computes a hash which, unlike <see cref="string.GetHashCode()"/>, is the same in every process.
    /// </summary>
    /// <param name="value">The value to hash.</param>
    /// <returns>The FNV-1a hash of the value.</returns>
    public static int StableHash(string value)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (char c in value)
            {
                hash = (hash ^ c) * 16777619;
            }

            return (int)hash;
        }
    }

    /// <summary>
    /// Splits a keyword string into its search terms: double-quoted phrases and single words.
    /// </summary>
    /// <param name="keyword">The keyword string.</param>
    /// <returns>The non-empty search terms.</returns>
    public static string[] ParseTerms(string? keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return [];
        }

        List<string> terms = [];
        foreach (Match match in TermRegex().Matches(keyword))
        {
            string term = (match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value).Trim();
            if (term.Length > 0)
            {
                terms.Add(term);
            }
        }

        return [.. terms];
    }

    /// <summary>
    /// Checks whether the given channel matches this rule.
    /// </summary>
    /// <param name="categoryId">The category of the channel.</param>
    /// <param name="name">The original name of the channel.</param>
    /// <returns>Whether the channel matches.</returns>
    public bool Matches(int? categoryId, string? name)
    {
        string[] terms = GetTerms();
        if (!Enabled || terms.Length == 0 || string.IsNullOrEmpty(name))
        {
            return false;
        }

        if (Categories.Count > 0 && (!categoryId.HasValue || !Categories.Contains(categoryId.Value)))
        {
            return false;
        }

        return terms.All(term => name.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    [GeneratedRegex("\"([^\"]*)\"?|([^\\s\"]+)")]
    private static partial Regex TermRegex();

    private string[] GetTerms()
    {
        // Rules are matched against every stream, so parse the keyword only when it changes.
        ParsedKeyword parsed = _parsed;
        if (!string.Equals(parsed.Source, Keyword, StringComparison.Ordinal))
        {
            parsed = new(Keyword, ParseTerms(Keyword));
            _parsed = parsed;
        }

        return parsed.Terms;
    }

    private sealed record ParsedKeyword(string Source, string[] Terms);
}
#pragma warning restore CA2227
