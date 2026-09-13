using NexusShell.App.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace NexusShell.App.Services
{
    /// <summary>
    /// Watches filtered terminal output and detects interactive prompts
    /// (numbered lists, yes/no questions, etc.) from ANY CLI program.
    /// Uses a debounce timer — only checks when output has settled (no new
    /// text for <see cref="DebounceMs"/> milliseconds), which is a strong
    /// signal that the child process is blocked waiting for stdin.
    /// </summary>
    public sealed class InteractivePromptDetector : IDisposable
    {
        // ── Configuration ─────────────────────────────────────────────────────
        private const int MaxBufferChars = 2500;
        private const int DebounceMs     = 300; // ms of silence before we check

        // ── State ─────────────────────────────────────────────────────────────
        private readonly StringBuilder _buffer = new(MaxBufferChars);
        private readonly Timer _debounceTimer;
        private readonly object _lock = new();
        private bool _suppressed;
        private bool _disposed;

        // ── Patterns ──────────────────────────────────────────────────────────

        /// <summary>Matches a Windows / PowerShell shell prompt at end of output.</summary>
        private static readonly Regex ShellPrompt = new(
            @"(?:PS\s+)?[A-Za-z]:\\.{0,200}>\s*$",
            RegexOptions.Compiled | RegexOptions.Multiline);

        /// <summary>Matches a numbered item: "1. text", "1) text", "1: text", "1- text".</summary>
        private static readonly Regex NumberedItem = new(
            @"^\s*(\d+)\s*[.):\]\->]+\s+(.+?)$",
            RegexOptions.Compiled | RegexOptions.Multiline);

        /// <summary>Matches a lettered item: "a) text", "a. text", "a- text".</summary>
        private static readonly Regex LetteredItem = new(
            @"^\s*([a-zA-Z])\s*[.):\]\->]+\s+(.+?)$",
            RegexOptions.Compiled | RegexOptions.Multiline);

        /// <summary>Matches (y/n), [Y/n], (yes/no), [Y/N] etc.</summary>
        private static readonly Regex YesNoBrackets = new(
            @"(.+?)\s*[\(\[]\s*([Yy](?:es)?)\s*[/|]\s*([Nn](?:o)?)\s*[\)\]]",
            RegexOptions.Compiled);

        /// <summary>Matches the reverse: (n/y), [N/Y] etc.</summary>
        private static readonly Regex NoYesBrackets = new(
            @"(.+?)\s*[\(\[]\s*([Nn](?:o)?)\s*[/|]\s*([Yy](?:es)?)\s*[\)\]]",
            RegexOptions.Compiled);

        /// <summary>Matches a TUI-selected item: line starting with '>', '❯', '›', '▶', '→', or '*' then text.</summary>
        private static readonly Regex TuiSelectedLine = new(
            @"^\s*[>❯›▶→*]\s*(.+)$",
            RegexOptions.Compiled);

        /// <summary>Matches a TUI-unselected item: 1+ leading spaces then non-space text.</summary>
        private static readonly Regex TuiUnselectedLine = new(
            @"^\s+(\S.+)$",
            RegexOptions.Compiled);

        // ── Events ────────────────────────────────────────────────────────────

        /// <summary>
        /// Raised (on a ThreadPool thread) when an interactive prompt is detected.
        /// The subscriber must marshal to the UI thread before showing the overlay.
        /// </summary>
        public event Action<DetectedPrompt>? PromptDetected;

        // ── Construction ──────────────────────────────────────────────────────

        public InteractivePromptDetector()
        {
            _debounceTimer = new Timer(OnDebounceElapsed, null, Timeout.Infinite, Timeout.Infinite);
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>Feed a new chunk of filtered terminal output into the detector.</summary>
        public void Feed(string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            lock (_lock)
            {
                if (_suppressed || _disposed) return;

                _buffer.Append(text);
                if (_buffer.Length > MaxBufferChars)
                    _buffer.Remove(0, _buffer.Length - MaxBufferChars);

                // Reset debounce timer — fires DebounceMs after the LAST chunk
                _debounceTimer.Change(DebounceMs, Timeout.Infinite);
            }
        }

        /// <summary>
        /// Pause detection (e.g. while the Interactive Screen overlay is open).
        /// </summary>
        public void Suppress()
        {
            lock (_lock)
            {
                _suppressed = true;
                _debounceTimer.Change(Timeout.Infinite, Timeout.Infinite); // cancel pending
            }
        }

        /// <summary>
        /// Resume detection and clear the buffer so old prompt text is not re-detected.
        /// Call this after the Interactive Screen overlay closes.
        /// </summary>
        public void Resume()
        {
            lock (_lock)
            {
                _buffer.Clear();
                _suppressed = false;
            }
        }

        // ── Timer callback ────────────────────────────────────────────────────

        private void OnDebounceElapsed(object? state)
        {
            string content;

            lock (_lock)
            {
                if (_suppressed || _disposed) return;
                content = _buffer.ToString();
                if (string.IsNullOrWhiteSpace(content)) return;

                // Suppress immediately to avoid re-detecting the same content
                _suppressed = true;
            }

            var prompt = TryDetect(content);

            if (prompt != null)
            {
                PromptDetected?.Invoke(prompt);
            }
            else
            {
                // Not a real prompt — resume so future output can be checked
                lock (_lock) _suppressed = false;
            }
        }

        // ── Detection orchestrator ────────────────────────────────────────────

        private DetectedPrompt? TryDetect(string content)
        {
            // If the output ends with a shell prompt, the command has finished
            // and there is no interactive prompt to handle.
            if (ShellPrompt.IsMatch(content))
                return null;

            return TryDetectTuiSelector(content)
                ?? TryDetectNumberedList(content)
                ?? TryDetectLetteredList(content)
                ?? TryDetectYesNo(content);
        }

        // ── Pattern 1: Numbered list ──────────────────────────────────────────

        private DetectedPrompt? TryDetectNumberedList(string content)
        {
            var matches = NumberedItem.Matches(content);
            if (matches.Count < 2) return null; // need ≥ 2 options

            return BuildListPrompt(content, matches);
        }

        // ── Pattern 2: Lettered list ──────────────────────────────────────────

        private DetectedPrompt? TryDetectLetteredList(string content)
        {
            var matches = LetteredItem.Matches(content);
            if (matches.Count < 2) return null;

            return BuildListPrompt(content, matches);
        }

        /// <summary>
        /// Shared builder for numbered/lettered option lists.
        /// Extracts options, a title, and validates that the output "looks like a prompt".
        /// </summary>
        private static DetectedPrompt? BuildListPrompt(string content, MatchCollection matches)
        {
            // Collect all matched items
            var items = new List<(string Key, string Text, int End)>();
            foreach (Match m in matches)
            {
                items.Add((m.Groups[1].Value, m.Groups[2].Value.Trim(), m.Index + m.Length));
            }

            // ── Validate: the tail of the content (after last item) must be empty
            //    or look like a prompt.  This avoids triggering on informational lists
            //    buried in the middle of larger output.
            int lastItemEnd = items[items.Count - 1].End;
            string tail = content.Substring(lastItemEnd).Trim();

            bool tailIsPrompt = string.IsNullOrEmpty(tail)
                             || tail.EndsWith(':')
                             || tail.EndsWith('?')
                             || tail.EndsWith('>');

            if (!tailIsPrompt) return null;

            // ── Extract title: collect preceding context lines above the first item
            int firstItemStart = matches[0].Index;
            string head = content.Substring(0, firstItemStart);
            var headLines = head.Split('\n');
            var titleParts = new List<string>();
            int linesCollected = 0;

            for (int i = headLines.Length - 1; i >= 0 && linesCollected < 4; i--)
            {
                string candidate = headLines[i].Trim().TrimEnd('\r');
                if (string.IsNullOrWhiteSpace(candidate)) continue;

                // Skip decorative / hint lines
                if (candidate.Contains("──") || candidate.Contains("==") 
                    || candidate.Contains("Navigate") || candidate.Contains("Complete"))
                    continue;

                if (candidate.StartsWith("? "))
                    candidate = candidate.Substring(2).Trim();

                titleParts.Insert(0, candidate);
                linesCollected++;
            }

            if (!string.IsNullOrWhiteSpace(tail))
            {
                string trimmedTail = tail.Trim();
                if (trimmedTail.StartsWith("? "))
                    trimmedTail = trimmedTail.Substring(2).Trim();
                titleParts.Add(trimmedTail);
            }

            string title = titleParts.Count > 0 
                ? string.Join(" ", titleParts) 
                : "Select an option";

            return new DetectedPrompt
            {
                Type    = PromptType.NumberedList,
                Title   = title,
                Options = items.Select(i => (i.Key, i.Text)).ToList()
            };
        }

        // ── Pattern 3: Yes / No ───────────────────────────────────────────────

        private DetectedPrompt? TryDetectYesNo(string content)
        {
            // Only check the last ~500 characters for yes/no
            string tail = content.Length > 500
                ? content.Substring(content.Length - 500)
                : content;

            // Try (y/n) style
            var m = YesNoBrackets.Match(tail);
            if (m.Success)
            {
                string question = m.Groups[1].Value;
                question = RetrieveFullQuestion(content, tail, m.Index, question);
                return BuildYesNo(question, m.Groups[2].Value, m.Groups[3].Value);
            }

            // Try (n/y) style (reversed)
            m = NoYesBrackets.Match(tail);
            if (m.Success)
            {
                string question = m.Groups[1].Value;
                question = RetrieveFullQuestion(content, tail, m.Index, question);
                return BuildYesNo(question, m.Groups[3].Value, m.Groups[2].Value);
            }

            return null;
        }

        private static DetectedPrompt BuildYesNo(string questionRaw, string yesToken, string noToken)
        {
            string question = questionRaw.Trim();
            // Strip leading '? ' marker used by some CLIs (e.g. Inquirer.js)
            if (question.StartsWith("? "))
                question = question.Substring(2);
            if (string.IsNullOrWhiteSpace(question))
                question = "Confirm?";

            string yesValue = yesToken.ToLowerInvariant(); // "y" or "yes"
            string noValue  = noToken.ToLowerInvariant();  // "n" or "no"

            return new DetectedPrompt
            {
                Type    = PromptType.YesNo,
                Title   = question,
                Options = new List<(string Value, string DisplayText)>
                {
                    (yesValue, "Yes"),
                    (noValue,  "No")
                }
            };
        }

        /// <summary>
        /// Collects up to 3 non-empty preceding lines from the terminal buffer to construct
        /// a fully descriptive question for screen readers when a short/generic prompt is matched.
        /// </summary>
        private static string RetrieveFullQuestion(string content, string tail, int matchIndexInTail, string matchedQuestion)
        {
            string question = matchedQuestion.Trim();
            // Clean leading '? ' if present
            if (question.StartsWith("? "))
                question = question.Substring(2).Trim();

            // We want to collect the lines above the match in content
            int matchIndexInContent = content.Length - tail.Length + matchIndexInTail;
            if (matchIndexInContent <= 0) return question;

            string precedingText = content.Substring(0, matchIndexInContent);
            var lines = precedingText.Split('\n')
                .Select(l => l.Trim().TrimEnd('\r'))
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();

            if (lines.Count == 0) return question;

            // Let's collect up to 3 preceding lines that are not generic prompts or decor
            var questionLines = new List<string>();
            int added = 0;
            for (int i = lines.Count - 1; i >= 0 && added < 3; i--)
            {
                string line = lines[i];
                // Skip separator lines or hint lines
                if (line.Contains("──") || line.Contains("==") || line.Contains("Navigate") || line.Contains("Complete"))
                    continue;

                questionLines.Insert(0, line);
                added++;
            }

            if (questionLines.Count > 0)
            {
                // Append the matched question if it is not generic and not already present
                if (!string.IsNullOrEmpty(question) && !question.Equals("confirm", StringComparison.OrdinalIgnoreCase) && !question.Equals("select", StringComparison.OrdinalIgnoreCase))
                {
                    if (!questionLines.Contains(question))
                        questionLines.Add(question);
                }
                return string.Join(" ", questionLines);
            }

            return question;
        }

        // ── Pattern 4: TUI selector menu (‘>’ prefix) ───────────────────────

        /// <summary>
        /// Detects cursor-based TUI menus where one item is marked with ‘>’.
        /// Common in modern CLI tools (Inquirer.js, agy, npm, etc.).
        /// </summary>
        private DetectedPrompt? TryDetectTuiSelector(string content)
        {
            var lines = content.Split('\n');

            // Find the LAST line matching the '>' selected-item pattern
            int selectedLineIdx = -1;
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                string trimmed = lines[i].TrimEnd('\r');
                if (TuiSelectedLine.IsMatch(trimmed))
                {
                    selectedLineIdx = i;
                    break;
                }
            }

            if (selectedLineIdx < 0) return null;

            // Collect menu items: expand up and down from the selected line
            var items = new List<(int LineIdx, string Text, bool IsSelected)>();

            // The selected line itself
            var selMatch = TuiSelectedLine.Match(lines[selectedLineIdx].TrimEnd('\r'));
            if (!selMatch.Success) return null;
            items.Add((selectedLineIdx, CleanTuiText(selMatch.Groups[1].Value), true));

            // Scan upward
            for (int i = selectedLineIdx - 1; i >= 0; i--)
            {
                string line = lines[i].TrimEnd('\r');
                if (string.IsNullOrWhiteSpace(line)) break;

                var um = TuiUnselectedLine.Match(line);
                if (um.Success)
                    items.Insert(0, (i, CleanTuiText(um.Groups[1].Value), false));
                else
                {
                    // Could be another selected line (scrolled TUI artifact) — try it
                    var sm = TuiSelectedLine.Match(line);
                    if (sm.Success)
                        items.Insert(0, (i, CleanTuiText(sm.Groups[1].Value), false));
                    else
                        break;
                }
            }

            // Scan downward
            for (int i = selectedLineIdx + 1; i < lines.Length; i++)
            {
                string line = lines[i].TrimEnd('\r');
                if (string.IsNullOrWhiteSpace(line)) break;

                var um = TuiUnselectedLine.Match(line);
                if (um.Success)
                    items.Add((i, CleanTuiText(um.Groups[1].Value), false));
                else
                {
                    var sm = TuiSelectedLine.Match(line);
                    if (sm.Success)
                        items.Add((i, CleanTuiText(sm.Groups[1].Value), false));
                    else
                        break;
                }
            }

            if (items.Count < 2) return null;

            // Find the current-selection index
            int currentIndex = items.FindIndex(it => it.IsSelected);
            if (currentIndex < 0) currentIndex = 0;

            // Extract a title from above the first item
            int firstLine = items[0].LineIdx;
            var titleParts = new List<string>();
            int linesCollected = 0;
            for (int i = firstLine - 1; i >= 0 && linesCollected < 4; i--)
            {
                string candidate = lines[i].TrimEnd('\r').Trim();
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                
                // Skip decorative lines and keyboard-hint lines
                if (candidate.Contains("\u2500") || candidate.Contains("\u2501")
                    || candidate.Contains("Navigate")
                    || candidate.Contains("\u2191") || candidate.Contains("\u2193")
                    || candidate.StartsWith("Keyboard")
                    || candidate.Contains("enter Select")
                    || candidate.Contains("for shortcuts"))
                    continue;

                // Strip leading '? ' from the title parts
                if (candidate.StartsWith("? "))
                    candidate = candidate.Substring(2).Trim();

                titleParts.Insert(0, candidate);
                linesCollected++;
            }

            string title = titleParts.Count > 0 
                ? string.Join(" ", titleParts) 
                : "Select an option";

            return new DetectedPrompt
            {
                Type         = PromptType.TuiSelector,
                Title        = title,
                CurrentIndex = currentIndex,
                Options      = items.Select((it, idx) => (idx.ToString(), it.Text)).ToList()
            };
        }

        /// <summary>Strips trailing annotations like " (current)" from TUI item text.</summary>
        private static string CleanTuiText(string raw)
        {
            string text = raw.Trim();
            // Remove trailing (current) / (active) / (default) annotations
            int parenStart = text.LastIndexOf('(');
            if (parenStart > 0 && text.EndsWith(')'))
            {
                string annotation = text.Substring(parenStart).ToLowerInvariant();
                if (annotation.Contains("current") || annotation.Contains("active")
                    || annotation.Contains("default") || annotation.Contains("selected"))
                {
                    text = text.Substring(0, parenStart).TrimEnd();
                }
            }
            return text;
        }

        // ── Dispose ───────────────────────────────────────────────────────────

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
            }
            _debounceTimer.Dispose();
        }
    }
}
