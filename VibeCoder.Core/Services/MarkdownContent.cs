using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using VibeCoder.Core.Models;

namespace VibeCoder.Core.Services
{
    /// <summary>Markdown builder helpers (tables, task list).</summary>
    public static class MarkdownContent
    {
        /// <summary>Escapes a Markdown table cell value.</summary>
        public static string EscapeCell(string value)
        {
            if (value == null) return "";
            return value
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Replace("|", "\\|");
        }

        /// <summary>Builds a Markdown table; the first row is treated as the header.</summary>
        public static string Table(IEnumerable<IReadOnlyList<string>> rows)
        {
            var list = rows?.ToList() ?? new List<IReadOnlyList<string>>();
            if (list.Count == 0) return "";
            int columnCount = list.Max(r => r?.Count ?? 0);
            if (columnCount == 0) return "";

            var sb = new StringBuilder();
            for (int r = 0; r < list.Count; r++)
            {
                var row = list[r];
                sb.Append("| ");
                for (int c = 0; c < columnCount; c++)
                {
                    var cell = row != null && c < row.Count ? row[c] : "";
                    sb.Append(EscapeCell(cell));
                    if (c < columnCount - 1) sb.Append(" | ");
                }
                sb.AppendLine(" |");
                if (r == 0)
                {
                    sb.Append("|");
                    for (int c = 0; c < columnCount; c++) sb.Append(" --- |");
                    sb.AppendLine();
                }
            }
            return sb.ToString();
        }


    }
}
