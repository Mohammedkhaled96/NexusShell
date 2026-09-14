using System;
using System.Text.RegularExpressions;

namespace NexusShell.App.Services
{
    public sealed class TuiSelectionTracker
    {
        // Matches ANSI Reverse Video (\x1b[7m) or Highlighted Backgrounds
        private static readonly Regex AnsiReverseVideo = new Regex(
            @"\x1B\[(?:[0-9;]*;)?(?:7|4[0-7]|10[0-7]|48;[25];[^m]+)m(.*?)(?:\x1B\[(?:27|0)m|$)",
            RegexOptions.Compiled | RegexOptions.Singleline);

        // Normalize cursor movements (e.g. \x1b[1B, \x1b[A, \x1b[2K, \r) into newlines
        private static readonly Regex CursorMoveToNewline = new Regex(
            @"\x1B\[\d*[A-EHFG]|\x1B\[2K|\r+",
            RegexOptions.Compiled);

        // Strip all remaining ANSI codes
        private static readonly Regex AnsiEscapeRegex = new Regex(
            @"\x1B\[[0-9;?=>]*[a-zA-Z]|\x1B\].*?(\x07|\x1B\\)|\x1B[@-Z\\-_]",
            RegexOptions.Compiled);

        // Matches bullet/pointer indicators anywhere at the start of a candidate line
        private static readonly Regex PointerLineRegex = new Regex(
            @"^\s*(?:[>❯›▶→*●◉⦿✔✓]|\[[xX•]\]|\(\*\))\s*(.+)$",
            RegexOptions.Compiled);

        private string _lastAnnouncedText = string.Empty;
        private long _lastAnnounceTicks = 0;

        public string? ExtractSelectedLine(string rawChunk)
        {
            if (string.IsNullOrWhiteSpace(rawChunk)) return null;

            // Strategy 1: Check for ANSI Reverse Video / Inverted Highlight (\x1b[7m)
            var reverseMatches = AnsiReverseVideo.Matches(rawChunk);
            if (reverseMatches.Count > 0)
            {
                string highlightedRaw = reverseMatches[reverseMatches.Count - 1].Groups[1].Value;
                string cleanHighlight = CleanLineText(highlightedRaw);
                if (IsValidOptionText(cleanHighlight))
                {
                    return RecordAndReturn(cleanHighlight);
                }
            }

            // Strategy 2: Normalize ANSI cursor moves to lines and check for pointers/bullets
            string normalized = CursorMoveToNewline.Replace(rawChunk, "\n");
            string[] lines = normalized.Split(new[] { "\n" }, StringSplitOptions.RemoveEmptyEntries);

            for (int i = lines.Length - 1; i >= 0; i--)
            {
                string line = lines[i];
                string stripped = AnsiEscapeRegex.Replace(line, "").Trim();

                var match = PointerLineRegex.Match(stripped);
                if (match.Success)
                {
                    string candidate = CleanLineText(match.Groups[1].Value);
                    if (IsValidOptionText(candidate))
                    {
                        return RecordAndReturn(candidate);
                    }
                }
                else if (stripped.StartsWith("●") || stripped.StartsWith("◉") || stripped.StartsWith("✔") || stripped.StartsWith("▸"))
                {
                    string candidate = CleanLineText(stripped);
                    if (IsValidOptionText(candidate))
                    {
                        return RecordAndReturn(candidate);
                    }
                }
            }

            return null;
        }

        private static string CleanLineText(string raw)
        {
            string clean = AnsiEscapeRegex.Replace(raw, " ").Trim();
            // Remove bullets and brackets cleanly
            clean = Regex.Replace(clean, @"^[>❯›▶→*●◉⦿✔✓•▸\-\|]+\s*", "");
            clean = clean.Trim('[', ']', '(', ')', ' ');
            clean = Regex.Replace(clean, @"\s+", " ");
            return clean.Trim();
        }

        private static bool IsValidOptionText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (text.Length < 2) return false;
            if (text.StartsWith("──") || text.StartsWith("==") || text.StartsWith("..") || text.StartsWith("--")) return false;
            if (text.Equals("Cancel", StringComparison.OrdinalIgnoreCase) || text.Equals("esc Cancel", StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        private string? RecordAndReturn(string text)
        {
            long now = Environment.TickCount64;
            // Deduplicate if identical within 100ms
            if (text == _lastAnnouncedText && (now - _lastAnnounceTicks) < 100)
            {
                return null;
            }

            _lastAnnouncedText = text;
            _lastAnnounceTicks = now;
            return text;
        }

        public void Reset()
        {
            _lastAnnouncedText = string.Empty;
            _lastAnnounceTicks = 0;
        }
    }
}
