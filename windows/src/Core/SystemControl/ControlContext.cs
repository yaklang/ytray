#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace YTray.Core.SystemControl
{
    // Every response remains a complete snapshot. No deltas or per-client history.
    internal static class ControlContext
    {
        internal sealed class View
        {
            internal string Text = "", FocusText = "";
            internal JObject? Focus;
            internal int? FocusIndex;
            internal int Shown, Total, DuplicateLinksOmitted;
            internal bool Truncated;
        }
        internal static View Format(JObject source, bool full = false, bool query = false, ControlRegion? region = null, string? textQuery = null)
        {
            var elements = (source["elements"] as JArray ?? new JArray()).OfType<JObject>().ToArray();
            var result = new View { Total = elements.Length, Truncated = source["truncated"]?.Value<bool>() == true };
            if (region != null && (source["focus"]?["region_text_truncated"]?.Value<bool>() == true || source["focus"]?["region_text_unavailable"]?.Value<bool>() == true)) result.Truncated = true;
            SummarizeFocus(source["focus"] as JObject, elements, result, region);
            if (full && region == null && source["tree_markdown"] is JValue markdown) {
                result.Text = markdown.Value<string>() ?? ""; result.Shown = elements.Length; return result;
            }
            if (elements.Length == 0 && region == null) {
                result.Text = Clip((string?)source["tree_markdown"] ?? "", full ? 30000 : 6000); return result;
            }
            var viewport = source["window_bounds"] as JObject;
            var covered = region == null ? new HashSet<int>() : (source["focus"]?["region_covered_controls"] as JArray ?? new JArray()).Values<int>().ToHashSet();
            var parents = elements.Where(e => e["parent_index"] != null).Select(e => (int)e["parent_index"]!).ToHashSet();
            var web = elements.Any(e => e["in_web_content"]?.Value<bool>() == true);
            var candidates = elements.Where(e => region == null || region.Includes(e) && !covered.Contains((int?)e["element_index"] ?? -1)).Where(e => full || query || Visible(e, viewport)).Where(e => {
                if (full || query) return true;
                var role = Role(e);
                if (new[] { "window", "pane", "toolbar", "separator", "image" }.Contains(role)) return false;
                if (role == "group" && (parents.Contains((int?)e["element_index"] ?? -1) || string.IsNullOrWhiteSpace((string?)e["label"]))) return false;
                // Web pages carry their native address field and tabs, while bookmarks,
                // extension buttons and window chrome remain available in query/full.
                if (web && e["in_web_content"]?.Value<bool>() != true && role != "document" && !Editable(e) && role != "tabitem") return false;
                return true;
            }).ToArray();
            var beforeDedup = candidates;
            if (!full) candidates = DeduplicateLinks(candidates, result.FocusIndex);
            result.DuplicateLinksOmitted = beforeDedup.Length - candidates.Length;
            var controls = candidates.Where(e => Role(e) != "text" && Role(e) != "document").ToArray();
            int Rank(JObject e) => (int?)e["element_index"] == result.FocusIndex && result.FocusIndex != null ? -2 : Priority(e);
            var selected = (full || query ? controls : controls.OrderBy(Rank).ThenBy(e => (int?)e["element_index"]).Take(80)).ToArray();
            var names = selected.Select(e => (string?)e["label"] ?? "").Where(s => s.Length != 0).ToHashSet(StringComparer.Ordinal);
            foreach (var hidden in elements.Where(e => covered.Contains((int?)e["element_index"] ?? -1))) if ((string?)hidden["label"] is string hiddenLabel) names.Add(hiddenLabel);
            // Labels belonging to discarded aliases must not reappear as passive text.
            foreach (var alias in beforeDedup.Except(candidates)) if ((string?)alias["label"] is string name) names.Add(name);
            var documents = candidates.Where(e => region == null && Role(e) == "document").Take(full ? 1000 : 2);
            var textBudget = full ? 30000 : region != null ? 3000 : 1200;
            var texts = new List<JObject>();
            foreach (var item in candidates.Where(e => Role(e) == "text")) {
                var label = (string?)item["label"] ?? "";
                if (string.IsNullOrWhiteSpace(label) || !names.Add(label)) continue;
                var length = Math.Min(label.Length, full ? 4000 : region != null ? 600 : 180);
                if (length > textBudget) { result.Truncated = true; continue; }
                texts.Add(item); textBudget -= length;
            }
            var rows = documents.Concat(selected).Concat(texts).OrderBy(e => (int?)e["element_index"]).ToArray();
            // Some native providers expose status/validation labels only as unindexed
            // markdown text. Keep a short excerpt so text-only callers can verify effects.
            var passive = new List<string>();
            if (region == null) {
                var positioned = elements.Where(e => ControlRegion.TryFrame(e["frame"] as JObject, out _)).ToArray();
                var outsideLabels = positioned.Where(e => !Visible(e, viewport)).Select(e => (string?)e["label"] ?? "")
                    .Except(positioned.Where(e => Visible(e, viewport)).Select(e => (string?)e["label"] ?? ""), StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
                foreach (Match match in Regex.Matches((string?)source["tree_markdown"] ?? "", @"(?m)^\s*-\s+(?:Text|Heading|Label|StaticText)\s+""(?<label>.*)""(?:\s+\[.*\])?\s*$")) {
                    var label = match.Groups["label"].Value;
                    if (!query && outsideLabels.Contains(label)) continue;
                    if (!names.Add(label)) continue;
                    passive.Add(Clip(label, 140));
                    if (passive.Count >= 8) break;
                }
            } else {
                // Unindexed markdown has no position. Use bounded UIA labels with
                // verified geometry instead, so unrelated sidebars never leak in.
                foreach (var item in (source["focus"]?["region_text"] as JArray ?? new JArray()).OfType<JObject>()) {
                    var label = (string?)item["text"] ?? "";
                    if (!region.Includes(item) || string.IsNullOrWhiteSpace(label) || !names.Add(label)) continue;
                    if (textQuery != null && !label.Contains(textQuery, StringComparison.OrdinalIgnoreCase)) continue;
                    var text = Clip(label, Math.Min(600, Math.Max(0, textBudget)));
                    if (text.Length == 0 || text.Length > textBudget) { result.Truncated = true; continue; }
                    passive.Add(text); textBudget -= text.Length;
                }
            }
            var excerpt = passive.Count == 0 ? "" : "Text: " + string.Join(" | ", passive);
            var lines = new List<(int Index, string Text)>(); var used = 0; var limit = (full ? 30000 : 6000) - excerpt.Length - result.FocusText.Length - 1;
            foreach (var element in rows.OrderBy(e => Role(e) == "document" ? -1 : Rank(e) == -2 ? -2 : Role(e) == "text" ? 3 : Rank(e) == 3 ? 4 : Rank(e))) {
                var line = Line(element, full, region != null);
                if (used + line.Length + 1 > limit) { result.Truncated = true; continue; }
                lines.Add(((int?)element["element_index"] ?? -1, line)); used += line.Length + 1; result.Shown++;
            }
            if (selected.Length < controls.Length) result.Truncated = true;
            result.Text = string.Join("\n", lines.OrderBy(line => line.Index).Select(line => line.Text));
            if (excerpt.Length != 0) result.Text += "\n" + excerpt;
            if (region != null && result.Text.Length == 0) result.Text = source["focus"]?["region_text_unavailable"]?.Value<bool>() == true
                ? "Region text unavailable; retry a smaller region or use a screenshot." : "No accessible text or controls in this region.";
            return result;
        }
        private static JObject[] DeduplicateLinks(JObject[] elements, int? focused)
        {
            string? Destination(JObject e) {
                if (Role(e) != "hyperlink") return null;
                var value = (string?)e["value"] ?? (string?)e["url"] ?? (string?)e["label"];
                return Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https") ? uri.AbsoluteUri : null;
            }
            var omitted = new HashSet<int>();
            foreach (var group in elements.Where(e => Destination(e) != null).GroupBy(Destination, StringComparer.Ordinal)) {
                // Same destination, one usable original index. Prefer the focused
                // link, then a descriptive title over URL/thumbnail/brand aliases.
                var keep = group.OrderBy(e => focused != null && (int?)e["element_index"] == focused ? 0 : 1)
                    .ThenByDescending(e => Uri.TryCreate((string?)e["label"], UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https") ? 0 : Math.Min(160, ((string?)e["label"] ?? "").Length))
                    .ThenBy(e => (int?)e["element_index"]).First();
                foreach (var alias in group) if (!ReferenceEquals(alias, keep) && alias["element_index"] != null) omitted.Add((int)alias["element_index"]!);
            }
            return elements.Where(e => e["element_index"] == null || !omitted.Contains((int)e["element_index"]!)).ToArray();
        }
        private static void SummarizeFocus(JObject? focus, JObject[] elements, View result, ControlRegion? region)
        {
            if (focus == null) return;
            var status = (string?)focus["status"] ?? "unavailable";
            result.Focus = new JObject { ["status"] = status };
            if (status != "focused") { result.FocusText = status == "other_window" ? "Focus: other window" : "Focus: unavailable"; return; }
            var role = (string?)focus["role"] ?? "Control"; var label = (string?)focus["label"] ?? "";
            var matches = elements.Where(e => string.Equals((string?)e["role"], role, StringComparison.OrdinalIgnoreCase) && (string?)e["label"] == label).ToArray();
            bool SameFrame(JObject e) => ControlRegion.TryFrame(e["frame"] as JObject, out var a) && ControlRegion.TryFrame(focus["frame"] as JObject, out var b)
                && Math.Abs(a.X - b.X) <= 3 && Math.Abs(a.Y - b.Y) <= 3 && Math.Abs(a.Width - b.Width) <= 3 && Math.Abs(a.Height - b.Height) <= 3;
            var exact = (matches.Length == 0 ? elements.Where(e => string.Equals((string?)e["role"], role, StringComparison.OrdinalIgnoreCase)) : matches).Where(SameFrame).ToArray();
            var control = matches.Length == 1 ? matches[0] : exact.Length == 1 ? exact[0] : null;
            result.FocusIndex = (int?)control?["element_index"];
            if (result.FocusIndex != null) result.Focus["element_index"] = result.FocusIndex;
            result.FocusText = "Focus: " + (result.FocusIndex != null ? "[" + result.FocusIndex + "]" : role + " " + JsonConvert.SerializeObject(Clip(label, 60)));
            if (region != null && !region.Includes(focus)) {
                result.Focus["outside_region"] = true; result.FocusText += " (outside region)"; return;
            }
            var selection = (string?)focus["selection_state"] ?? "unavailable";
            result.Focus["selection_state"] = selection;
            if (selection == "selected" && region == null) {
                var text = Clip((string?)focus["selected_text"] ?? "", 160);
                result.FocusText += " | selected: " + JsonConvert.SerializeObject(text) + (focus["selection_truncated"]?.Value<bool>() == true ? "…" : "");
            } else result.FocusText += " | selection: " + selection;
        }
        private static string Role(JObject e) => ((string?)e["role"] ?? "").ToLowerInvariant();
        private static bool Editable(JObject e) => Role(e) is "edit" or "combobox" or "textbox";
        private static int Priority(JObject e) => Editable(e) ? 0 : Role(e) is "button" or "checkbox" or "radiobutton" or "menuitem" ? 1 : Role(e) == "tabitem" ? 2 : 3;
        private static bool Visible(JObject e, JObject? viewport)
        {
            if (e["offscreen"]?.Value<bool>() == true || e["is_offscreen"]?.Value<bool>() == true) return false;
            if (!(e["frame"] is JObject f) || viewport == null) return true;
            var x = (double?)f["x"] ?? 0; var y = (double?)f["y"] ?? 0;
            var w = (double?)f["w"] ?? 0; var h = (double?)f["h"] ?? 0;
            var left = (double?)viewport["x"] ?? 0; var top = (double?)viewport["y"] ?? 0;
            return w > 0 && h > 0 && x + w > left && y + h > top
                && x < left + ((double?)viewport["width"] ?? double.MaxValue) && y < top + ((double?)viewport["height"] ?? double.MaxValue);
        }
        private static string Line(JObject e, bool full, bool region)
        {
            var label = Clip((string?)e["label"] ?? "", full ? 4000 : region && Role(e) == "text" ? 600 : 180);
            var line = "[" + e["element_index"] + "] " + ((string?)e["role"] ?? "Control");
            if (label.Length != 0) line += " " + JsonConvert.SerializeObject(label);
            if (e["value"] != null && Role(e) != "document" && Role(e) != "hyperlink")
                line += " = " + JsonConvert.SerializeObject(Clip((string)e["value"]!, full ? 8000 : 180));
            foreach (var state in new[] { "checked", "selected", "expanded", "focused" })
                if (e[state]?.Type == JTokenType.Boolean) line += " " + state + "=" + e[state]!.ToString(Formatting.None);
            if (e["enabled"]?.Value<bool>() == false) line += " disabled";
            return line;
        }
        private static string Clip(string text, int length)
        {
            if (length <= 0) return "";
            text = Regex.Replace(text, @"\s+", " ").Trim();
            if (text.Length <= length) return text;
            if (char.IsHighSurrogate(text[length - 1])) length--;
            return text.Substring(0, length) + "…";
        }
    }
}
