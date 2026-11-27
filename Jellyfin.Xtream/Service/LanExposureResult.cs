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
/// The result of a <see cref="LanExposureCheck"/>.
/// </summary>
/// <param name="Reachable">Whether Jellyfin could connect into the local network, or <c>null</c> when it could not be tested.</param>
/// <param name="Target">The address that was tested.</param>
/// <param name="Detail">A description of the result.</param>
public record LanExposureResult(bool? Reachable, string? Target, string Detail)
{
    /// <summary>
    /// Gets when the check ran.
    /// </summary>
    public DateTime CheckedUtc { get; init; } = DateTime.UtcNow;
}
