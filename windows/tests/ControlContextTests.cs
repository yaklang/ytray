#nullable enable
using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using YTray.Core.SystemControl;

namespace YTray.Tests
{
    [TestClass]
    public class ControlContextTests
    {
        private static JObject Element(int index, string role, string label, int y = 140, bool web = true) => new JObject {
            ["element_index"] = index, ["role"] = role, ["label"] = label, ["enabled"] = true, ["in_web_content"] = web,
            ["frame"] = new JObject { ["x"] = 30, ["y"] = y, ["w"] = 120, ["h"] = 24 }
        };
        private static JObject Page(params JObject[] elements) => new JObject {
            ["window_bounds"] = new JObject { ["x"] = 10, ["y"] = 20, ["width"] = 640, ["height"] = 480 }, ["elements"] = new JArray(elements)
        };
        [TestMethod, TestCategory("ControlProtocol")]
        public void CompactSnapshotKeepsUsableControlsAndOriginalIndexesWithoutChromeOrOffscreenNoise()
        {
            var address = Element(6, "Edit", "Address", 60, false); address["value"] = "example.test";
            var bookmark = Element(22, "Button", "Irrelevant bookmark", 90, false);
            var search = Element(45, "Edit", "Search"); search["value"] = "yaklang.io";
            var container = Element(50, "Group", "wrapper"); var send = Element(63, "Button", "Search now"); send["parent_index"] = 50;
            var error = Element(74, "Text", "No matching records");
            var hidden = Element(87, "Button", "Hidden extension", 900); var zero = Element(91, "Edit", "Hidden textbox"); zero["frame"]!["w"] = 0;
            var source = Page(address, bookmark, search, container, send, error, hidden, zero);
            source["tree_markdown"] = "- Text \"Hidden extension\"\n- Text \"Current notification\"\n";
            var view = ControlContext.Format(source);
            StringAssert.Contains(view.Text, "[45] Edit \"Search\" = \"yaklang.io\"");
            StringAssert.Contains(view.Text, "[63] Button \"Search now\""); StringAssert.Contains(view.Text, "No matching records");
            StringAssert.Contains(view.Text, "[6] Edit");
            foreach (var omitted in new[] { "Irrelevant bookmark", "wrapper", "Hidden extension", "Hidden textbox" }) Assert.IsFalse(view.Text.Contains(omitted), view.Text);
            Assert.AreEqual(4, view.Shown); Assert.AreEqual(8, view.Total);
            StringAssert.Contains(view.Text, "Current notification");
            var queried = ControlContext.Format(Page(hidden), query: true); StringAssert.Contains(queried.Text, "[87] Button");
        }
        [TestMethod, TestCategory("ControlProtocol")]
        public void DenseSnapshotsStayBoundedAndKeepInputsIncludingUnicodeAndState()
        {
            var links = Enumerable.Range(0, 200).Select(i => Element(i, "Hyperlink", "Result " + i + " " + new string('x', 300))).ToArray();
            var input = Element(450, "Edit", "Message 🚀"); input["value"] = "你好" + new string('v', 176); input["enabled"] = false;
            var view = ControlContext.Format(Page(links.Concat(new[] { input }).ToArray()));
            Assert.IsTrue(view.Text.Length <= 6000); Assert.IsTrue(view.Truncated);
            StringAssert.Contains(view.Text, "[450] Edit"); StringAssert.Contains(view.Text, "你好"); StringAssert.Contains(view.Text, "disabled");
            Assert.IsFalse(view.Text.Contains("element_token")); Assert.IsFalse(view.Text.Contains("actions="));
        }
        [TestMethod, TestCategory("ControlProtocol")]
        public void FullSnapshotRetainsNativeTreeAndAllIndexesForInspection()
        {
            var source = Page(Element(99, "Button", "Below viewport", 1000));
            source["tree_markdown"] = "  - [99] Button \"Below viewport\" [actions=[invoke]]\n";
            var view = ControlContext.Format(source, full: true);
            Assert.AreEqual((string)source["tree_markdown"]!, view.Text); Assert.AreEqual(1, view.Shown);
        }
        [TestMethod, TestCategory("ControlProtocol")]
        public void NativeUnindexedStatusTextRemainsReadableWithoutRepeatingControlLabels()
        {
            var source = Page(Element(5, "Button", "Save", web: false));
            source["tree_markdown"] = "- [5] Button \"Save\"\n  - Text \"Save\"\n- Text \"Saved successfully\"\n- Text \"Count: 1\"\n";
            var view = ControlContext.Format(source);
            StringAssert.Contains(view.Text, "Saved successfully"); StringAssert.Contains(view.Text, "Count: 1");
            Assert.AreEqual(1, System.Text.RegularExpressions.Regex.Matches(view.Text, "Save\"").Count);
        }

        [TestMethod, TestCategory("ControlProtocol")]
        public void LinkAliasesCollapseToADescriptiveOriginalIndexWithoutMergingDifferentDestinations()
        {
            var title = Element(10, "Hyperlink", "Yak project documentation"); title["value"] = "https://EXAMPLE.test:443/docs?q=yak#intro";
            var raw = Element(11, "Hyperlink", "https://example.test/docs?q=yak#intro"); raw["value"] = "https://example.test/docs?q=yak#intro";
            var brand = Element(12, "Hyperlink", "Example"); brand["value"] = "https://example.test/docs?q=yak#intro";
            var next = Element(13, "Hyperlink", "Another anchor"); next["value"] = "https://example.test/docs?q=yak#next";
            var query = Element(14, "Hyperlink", "Another query"); query["value"] = "https://example.test/docs?q=yakit#intro";
            var action1 = Element(15, "Hyperlink", "Custom action"); var action2 = Element(16, "Hyperlink", "Custom action");
            var source = Page(title, raw, brand, next, query, action1, action2);
            var view = ControlContext.Format(source);
            Assert.AreEqual(2, view.DuplicateLinksOmitted); Assert.AreEqual(5, view.Shown);
            StringAssert.Contains(view.Text, "[10] Hyperlink"); StringAssert.Contains(view.Text, "[13] Hyperlink"); StringAssert.Contains(view.Text, "[14] Hyperlink");
            Assert.IsFalse(view.Text.Contains("[11]")); Assert.IsFalse(view.Text.Contains("[12]"));
            StringAssert.Contains(view.Text, "[15] Hyperlink"); StringAssert.Contains(view.Text, "[16] Hyperlink");
            source["focus"] = new JObject { ["status"] = "focused", ["role"] = "Hyperlink", ["label"] = (string)raw["label"]!, ["frame"] = raw["frame"]!.DeepClone() };
            var focused = ControlContext.Format(source);
            Assert.AreEqual(11, focused.FocusIndex); StringAssert.Contains(focused.Text, "[11] Hyperlink"); Assert.IsFalse(focused.Text.Contains("[10]"));
            Assert.AreEqual(7, ControlContext.Format(source, full: true).Shown, "Full must preserve every alias");
        }

        [TestMethod, TestCategory("ControlProtocol")]
        public void FocusSelectionSummaryRemainsShortAndDoesNotDuplicateSelectionInMetadata()
        {
            var input = Element(400, "Edit", "Message");
            var source = Page(Enumerable.Range(0, 100).Select(i => Element(i, "Hyperlink", "Link " + i)).Concat(new[] { input }).ToArray());
            source["focus"] = new JObject { ["status"] = "focused", ["role"] = "Edit", ["label"] = "Message", ["frame"] = input["frame"]!.DeepClone(),
                ["selection_state"] = "selected", ["selected_text"] = "你好 🚀 " + new string('x', 300), ["selection_truncated"] = true };
            var view = ControlContext.Format(source);
            Assert.AreEqual(400, view.FocusIndex); StringAssert.Contains(view.FocusText, "Focus: [400]"); StringAssert.Contains(view.FocusText, "你好 🚀");
            Assert.IsTrue(view.FocusText.Length < 220); Assert.IsNull(view.Focus!["selected_text"]);
            StringAssert.Contains(view.Text, "[400] Edit");
            source["focus"] = new JObject { ["status"] = "other_window" };
            var other = ControlContext.Format(source); Assert.IsNull(other.FocusIndex); Assert.AreEqual("Focus: other window", other.FocusText);
        }

        [TestMethod, TestCategory("ControlProtocol")]
        public void RegionsUseTheCurrentImageFrameWithoutPriorCapturesAndClipToWindow()
        {
            var source = new JObject { ["window_bounds"] = new JObject { ["x"] = -1920, ["y"] = 200, ["width"] = 2560, ["height"] = 1440 } };
            var options = new JObject { ["max_image_dimension"] = 1280, ["region"] = new JObject { ["x"] = 200, ["y"] = 100, ["width"] = 200, ["height"] = 100 } };
            var region = ControlRegion.Create(source, options)!;
            Assert.AreEqual(1280, region.ImageWidth); Assert.AreEqual(720, region.ImageHeight);
            Assert.AreEqual(-1520, region.ScreenBounds.X); Assert.AreEqual(400, region.ScreenBounds.Y); Assert.AreEqual(400, region.ScreenBounds.Width);
            options["region"]!["width"] = 5000;
            Assert.AreEqual(1080, ControlRegion.Create(source, options)!.ImageBounds.Value<double>("width"));
            options["region"]!["x"] = 1280;
            Assert.ThrowsExactly<YTray.Models.ControlException>(() => ControlRegion.Create(source, options));
        }

        [TestMethod, TestCategory("ControlProtocol")]
        public void RegionalReadsExcludeOutsideTextAndKeepGeometryVerifiedNativeStatusLabels()
        {
            var input = Element(45, "Edit", "Search"); input["value"] = "inside";
            var outside = Element(50, "Hyperlink", "Unrelated sidebar"); outside["frame"]!["x"] = 500;
            var source = Page(input, outside);
            var covered = Element(60, "Hyperlink", "Link underneath sticky header"); ((JArray)source["elements"]!).Add(covered);
            source["tree_markdown"] = "- Text \"Unpositioned sidebar text\"";
            source["focus"] = new JObject { ["status"] = "focused", ["role"] = "Hyperlink", ["label"] = "Unrelated sidebar", ["frame"] = outside["frame"]!.DeepClone(),
                ["selection_state"] = "selected", ["selected_text"] = "Outside selection", ["region_covered_controls"] = new JArray(60), ["region_text"] = new JArray(
                    new JObject { ["text"] = "Count: 1", ["frame"] = input["frame"]!.DeepClone() },
                    new JObject { ["text"] = "Link underneath sticky header", ["frame"] = covered["frame"]!.DeepClone() },
                    new JObject { ["text"] = "Outside status", ["frame"] = outside["frame"]!.DeepClone() }) };
            var options = new JObject { ["region"] = new JObject { ["x"] = 0, ["y"] = 100, ["width"] = 250, ["height"] = 100 } };
            var region = ControlRegion.Create(source, options)!;
            var view = ControlContext.Format(source, region: region);
            StringAssert.Contains(view.Text, "[45] Edit"); StringAssert.Contains(view.Text, "Count: 1");
            foreach (var omitted in new[] { "Unrelated sidebar", "Unpositioned sidebar text", "Outside status", "Outside selection", "Link underneath sticky header" }) Assert.IsFalse((view.Text + view.FocusText).Contains(omitted), omitted);
            StringAssert.Contains(view.FocusText, "outside region");
            var full = ControlContext.Format(source, full: true, region: region);
            Assert.IsFalse(full.Text.Contains("Unpositioned sidebar"), "Full within a region must not fall back to the whole-window markdown");
            var filtered = ControlContext.Format(source, query: true, region: region, textQuery: "missing"); Assert.IsFalse(filtered.Text.Contains("Count: 1"));
        }
    }
}
