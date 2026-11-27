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
namespace Jellyfin.Xtream.Api.Models;

/// <summary>
/// The result of checking whether the provider can be reached over HTTPS.
/// </summary>
public class HttpsCheckResponse
{
    /// <summary>
    /// Gets or sets a value indicating whether the provider answered correctly over HTTPS with a valid certificate.
    /// </summary>
    public bool Available { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the base URL was switched to HTTPS.
    /// </summary>
    public bool Applied { get; set; }

    /// <summary>
    /// Gets or sets the HTTPS base URL that worked, if any.
    /// </summary>
    public string? Url { get; set; }

    /// <summary>
    /// Gets or sets a description of the result.
    /// </summary>
    public string Detail { get; set; } = string.Empty;
}
