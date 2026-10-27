using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace VibeCoder.Core.Services
{
    /// <summary>
    /// Serializes interactive question answer options for persistence in the chat history
    /// database. Kept in Core (no WPF dependency) so the round-trip can be unit-tested.
    /// </summary>
    public static class QuestionOptionsCodec
    {
        /// <summary>Serializes the options to a JSON string for storage. Null or empty lists produce an empty string.</summary>
        public static string Encode(IReadOnlyList<string> options)
        {
            if (options == null || options.Count == 0)
                return "";

            return JsonConvert.SerializeObject(options);
        }

        /// <summary>Deserializes options from a JSON string. Null, empty or malformed input yields an empty list.</summary>
        public static IReadOnlyList<string> Decode(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return new List<string>();

            try
            {
                var options = JsonConvert.DeserializeObject<List<string>>(json);
                return options ?? new List<string>();
            }
            catch (Exception)
            {
                return new List<string>();
            }
        }
    }
}
