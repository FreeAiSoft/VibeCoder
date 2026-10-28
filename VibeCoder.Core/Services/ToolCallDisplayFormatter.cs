using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace VibeCoder.Core.Services
{
    /// <summary>
    /// Builds the chat text shown for tool-call messages: a one-line header with the tool
    /// name and its argument, followed by the tool output. Kept in Core so the formatting
    /// (and the collapse-to-preview logic) can be unit-tested without WPF.
    /// </summary>
    public static class ToolCallDisplayFormatter
    {
        /// <summary>Number of lines shown when a tool output is collapsed: the header line plus five result lines.</summary>
        public const int CollapsedPreviewLines = 6;

        /// <summary>Formats a tool execution into the full multi-line chat text (header + output).</summary>
        public static string Format(string toolName, JObject arguments, string result, string displayResult)
        {
            var name = toolName ?? "";
            switch (name)
            {
                case "read_files":
                    return "read_files:\n" + JoinLines(displayResult);
                case "list_files":
                    return FormatListFiles(arguments, result);
                case "search_codebase":
                    return FormatSearchCodebase(arguments, result);
                default:
                    var body = string.IsNullOrWhiteSpace(displayResult) ? result ?? "" : displayResult;
                    return name + " → " + body;
            }
        }

        /// <summary>True when <paramref name="fullText"/> has more than <paramref name="maxLines"/> lines.</summary>
        public static bool HasMore(string fullText, int maxLines)
        {
            if (string.IsNullOrEmpty(fullText))
                return false;
            return SplitLines(fullText).Count > maxLines;
        }

        /// <summary>Returns the first <paramref name="maxLines"/> lines of <paramref name="fullText"/> (or the full text if shorter).</summary>
        public static string Collapse(string fullText, int maxLines)
        {
            if (string.IsNullOrEmpty(fullText))
                return "";
            var lines = SplitLines(fullText);
            if (lines.Count <= maxLines)
                return fullText;
            return string.Join("\n", lines.Take(maxLines));
        }

        private static string FormatListFiles(JObject arguments, string result)
        {
            var path = arguments?["path"]?.Value<string>() ?? "";
            var recursive = arguments?["recursive"]?.Value<bool>() ?? false;

            var header = "list_files";
            if (!string.IsNullOrWhiteSpace(path))
                header += " → " + path;
            if (recursive)
                header += " (recursive)";
            return header + "\n" + (result ?? "");
        }

        private static string FormatSearchCodebase(JObject arguments, string result)
        {
            var queries = ParseQueries(arguments?["queries"]);
            var header = "search_codebase → " + string.Join(", ", queries.Select(q => "\"" + q + "\""));
            return header + "\n" + (result ?? "");
        }

        private static List<string> ParseQueries(JToken token)
        {
            var result = new List<string>();
            if (token is JArray array)
            {
                foreach (var item in array)
                {
                    var query = item?.Value<string>();
                    if (!string.IsNullOrWhiteSpace(query))
                        result.Add(query!);
                }
            }
            else
            {
                var single = token?.Value<string>();
                if (!string.IsNullOrWhiteSpace(single))
                    result.Add(single!);
            }
            return result;
        }

        private static string JoinLines(string text)
        {
            return string.Join("\n", SplitLines(text));
        }

        private static List<string> SplitLines(string text)
        {
            if (string.IsNullOrEmpty(text))
                return new List<string>();
            return text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
        }
    }
}
