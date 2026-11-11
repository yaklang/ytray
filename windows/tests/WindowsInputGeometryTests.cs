using System.Drawing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using YTray.Models;
using YTray.Native;

namespace YTray.Tests
{
    [TestClass]
    public class WindowsInputGeometryTests
    {
        [TestMethod, TestCategory("ControlProtocol")]
        public void PartiallyClippedControlsAreClickedInsideTheTargetWindow()
        {
            var window = new Rectangle(100, 100, 400, 300);
            foreach (var control in new[] {
                new Rectangle(200, 380, 150, 300), new Rectangle(480, 200, 300, 40),
                new Rectangle(-200, 200, 320, 40), new Rectangle(200, -200, 150, 320) }) {
                var point = WindowsInputGeometry.VisibleCenter(control, window);
                Assert.IsTrue(window.Contains(point), "Input escaped the target window: " + point);
                Assert.IsTrue(control.Contains(point), "Input escaped the selected control: " + point);
            }
            Assert.AreEqual(new Point(275, 390), WindowsInputGeometry.VisibleCenter(new Rectangle(200, 380, 150, 300), window));
        }

        [TestMethod, TestCategory("ControlProtocol")]
        public void HiddenAndZeroAreaControlsRefuseBeforeInput()
        {
            var window = new Rectangle(100, 100, 400, 300);
            foreach (var control in new[] { new Rectangle(500, 100, 80, 40), new Rectangle(200, 400, 80, 40), new Rectangle(120, 120, 0, 40), Rectangle.Empty }) {
                var error = Assert.ThrowsExactly<ControlException>(() => WindowsInputGeometry.VisibleCenter(control, window));
                Assert.AreEqual("ElementNotVisible", error.Code);
                Assert.AreEqual("NotStarted", error.ExecutionState);
            }
        }

        [TestMethod, TestCategory("ControlProtocol")]
        public void NegativeMonitorOriginsAndOnePixelIntersectionsRemainExact()
        {
            var window = new Rectangle(-1920, -1080, 1200, 800);
            Assert.AreEqual(new Point(-1919, -1000), WindowsInputGeometry.VisibleCenter(new Rectangle(-2000, -1020, 82, 40), window));
            Assert.AreEqual(new Point(-721, -281), WindowsInputGeometry.VisibleCenter(new Rectangle(-721, -281, 100, 100), window));
        }
    }
}
