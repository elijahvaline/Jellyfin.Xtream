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
using Jellyfin.Xtream.Configuration;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// The streams matched by a dynamic rule.
/// </summary>
/// <param name="Rule">The rule.</param>
/// <param name="Schedule">The schedule of the linked team, or <c>null</c> if none is linked or it could not be loaded.</param>
/// <param name="Streams">The matched streams.</param>
public sealed record RuleMatches(DynamicChannelRule Rule, TeamSchedule? Schedule, IReadOnlyList<MatchedChannel> Streams);
