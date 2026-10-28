using System;
using System.Globalization;

namespace VibeCoder.Core.Services
{
    /// <summary>
    /// Formats chat message timestamps for the message footer. Kept in Core so the
    /// formatting can be unit-tested without WPF.
    /// </summary>
    public static class ChatTimeFormatter
    {
        /// <summary>Converts a UTC timestamp to local time and formats it as "HH:mm:ss".</summary>
        public static string FormatLocalTime(DateTime utc)
        {
            return utc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        }
    }
}