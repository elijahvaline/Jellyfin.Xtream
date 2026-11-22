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

namespace Jellyfin.Xtream.Service;

/// <summary>
/// A team in a league.
/// </summary>
/// <param name="Id">The ESPN team id.</param>
/// <param name="Name">The display name, e.g. <c>Minnesota Timberwolves</c>.</param>
/// <param name="Abbreviation">The abbreviation, e.g. <c>MIN</c>.</param>
/// <param name="LogoUrl">The logo url, if known.</param>
public sealed record SportsTeam(string Id, string Name, string Abbreviation, string? LogoUrl);
