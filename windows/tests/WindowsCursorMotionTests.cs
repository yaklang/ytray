using System;
using System.Drawing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using YTray.Native;

namespace YTray.Tests
{
    [TestClass]
    public class WindowsCursorMotionTests
    {
        [TestMethod, TestCategory("ControlProtocol")]
        public void PathReachesExactTargetAndStartsAndStopsGently()
        {
            var path = new WindowsCursorMotion(new PointF(100, 300), new PointF(1500, 700), new Rectangle(0, 0, 1920, 1080), 1);
            Assert.AreEqual(new PointF(100, 300), path.Sample(0)); Assert.AreEqual(new PointF(1500, 700), path.Sample(1));
            double Distance(PointF a, PointF b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
            var cruise = Distance(path.Sample(.49), path.Sample(.51));
            Assert.IsTrue(Distance(path.Sample(0), path.Sample(.02)) < cruise * .05);
            Assert.IsTrue(Distance(path.Sample(.98), path.Sample(1)) < cruise * .05);
        }
        [TestMethod, TestCategory("ControlProtocol")]
        public void ArcIsCurvedAndRemainsInsideNegativeOriginDesktop()
        {
            var desktop = new Rectangle(-1920, -1080, 3840, 2160);
            var path = new WindowsCursorMotion(new PointF(-1800, -1000), new PointF(1800, -1000), desktop, 1);
            Assert.IsTrue(Math.Abs(path.Sample(.5).Y + 1000) > 5, "The path must not collapse to a straight chord");
            for (var index = 0; index <= 100; index++) {
                var point = path.Sample(index / 100.0);
                Assert.IsTrue(point.X >= desktop.Left && point.X < desktop.Right && point.Y >= desktop.Top && point.Y < desktop.Bottom);
            }
        }
        [TestMethod, TestCategory("ControlProtocol")]
        public void PaceScalesWithDistanceAndDpiWithoutDelayingStationarySteps()
        {
            var desktop = new Rectangle(0, 0, 4000, 2000); var from = new PointF(100, 100);
            var near = new WindowsCursorMotion(from, new PointF(240, 100), desktop, 1);
            var far = new WindowsCursorMotion(from, new PointF(1900, 100), desktop, 1);
            var highDpi = new WindowsCursorMotion(new PointF(200, 200), new PointF(480, 200), desktop, 2);
            Assert.AreEqual(near.DurationMilliseconds, highDpi.DurationMilliseconds);
            Assert.IsTrue(near.DurationMilliseconds >= 45 && far.DurationMilliseconds > near.DurationMilliseconds && far.DurationMilliseconds <= 150);
            Assert.AreEqual(0, new WindowsCursorMotion(from, from, desktop, 1).DurationMilliseconds);
        }
    }
}
