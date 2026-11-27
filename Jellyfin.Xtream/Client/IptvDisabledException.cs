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

namespace Jellyfin.Xtream.Client;

/// <summary>
/// Thrown instead of contacting the IPTV provider while the connection is switched off.
/// </summary>
public class IptvDisabledException : InvalidOperationException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="IptvDisabledException"/> class.
    /// </summary>
    public IptvDisabledException()
        : base("The IPTV connection is switched off in the Xtream plugin settings.")
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="IptvDisabledException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    public IptvDisabledException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="IptvDisabledException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The inner exception.</param>
    public IptvDisabledException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
