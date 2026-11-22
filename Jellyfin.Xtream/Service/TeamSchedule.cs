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

using System.Collections.Generic;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// The schedule of a team.
/// </summary>
/// <param name="Team">The team.</param>
/// <param name="TeamNames">The names by which the team itself may appear in a stream name.</param>
/// <param name="Games">The games, ordered by start time.</param>
public sealed record TeamSchedule(SportsTeam Team, IReadOnlyList<string> TeamNames, IReadOnlyList<SportsGame> Games);
