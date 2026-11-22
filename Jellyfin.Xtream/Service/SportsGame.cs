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

namespace Jellyfin.Xtream.Service;

/// <summary>
/// A scheduled game of a team.
/// </summary>
/// <param name="Id">The ESPN event id.</param>
/// <param name="StartUtc">The start time in UTC.</param>
/// <param name="TimeValid">Whether the start time is confirmed; otherwise only the date is meaningful.</param>
/// <param name="EndUtc">The expected end time in UTC.</param>
/// <param name="Name">The game name, e.g. <c>Brooklyn Nets at Minnesota Timberwolves</c>.</param>
/// <param name="ShortName">The short game name, e.g. <c>BKN @ MIN</c>.</param>
/// <param name="Broadcast">The broadcasting networks, if known.</param>
/// <param name="OpponentNames">The names by which the opponent may appear in a stream name.</param>
public sealed record SportsGame(
    string Id,
    DateTime StartUtc,
    bool TimeValid,
    DateTime EndUtc,
    string Name,
    string ShortName,
    string Broadcast,
    IReadOnlyList<string> OpponentNames);
