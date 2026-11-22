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

namespace Jellyfin.Xtream.Service;

/// <summary>
/// A planned smart refresh of the TV guide.
/// </summary>
/// <param name="TimeUtc">When the refresh runs, in UTC.</param>
/// <param name="Reason">Why it runs, e.g. <c>1 h before Brooklyn Nets at Minnesota Timberwolves</c>.</param>
public sealed record PlannedRun(DateTime TimeUtc, string Reason);
