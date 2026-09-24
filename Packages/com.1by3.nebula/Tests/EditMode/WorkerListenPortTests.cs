using NUnit.Framework;

namespace Nebula.Tests
{
    public sealed class WorkerListenPortTests
    {
        [Test]
        public void TheCommandLineWinsOverPortAndTheConfig()
        {
            var r = WorkerListenPort.Resolve("7401", "8080", 7100, 3);
            Assert.AreEqual(7401, r.Port);
            Assert.AreEqual(WorkerListenPort.Source.CommandLine, r.From);
            Assert.IsNull(r.Warning);
        }

        [Test]
        public void PortIsUsedWhenThereIsNoSwitch()
        {
            var r = WorkerListenPort.Resolve(null, "8080", 7100, 3);
            Assert.AreEqual(8080, r.Port);
            Assert.AreEqual(WorkerListenPort.Source.Environment, r.From);
            Assert.AreEqual("PORT", r.Describe);
            Assert.IsNull(r.Warning);
        }

        [Test]
        public void TheConfigIsUsedWhenNeitherIsSet()
        {
            var r = WorkerListenPort.Resolve(null, null, 7100, 3);
            Assert.AreEqual(7103, r.Port);
            Assert.AreEqual(WorkerListenPort.Source.Config, r.From);
            Assert.IsNull(r.Warning);
            Assert.AreEqual(7103, WorkerListenPort.Resolve(null, "", 7100, 3).Port, "an empty PORT counts as unset");
            Assert.IsNull(WorkerListenPort.Resolve(null, "", 7100, 3).Warning);
        }

        [TestCase("http")]
        [TestCase("0")]
        [TestCase("65536")]
        [TestCase("-80")]
        [TestCase("80.5")]
        public void AnInvalidPortIsIgnoredWithAWarning(string value)
        {
            var r = WorkerListenPort.Resolve(null, value, 7100, 3);
            Assert.AreEqual(7103, r.Port);
            Assert.AreEqual(WorkerListenPort.Source.Config, r.From);
            StringAssert.Contains("PORT", r.Warning);
        }

        [Test]
        public void AnInvalidSwitchFallsThroughToPortWithAWarning()
        {
            var r = WorkerListenPort.Resolve("99999", "8080", 7100, 3);
            Assert.AreEqual(8080, r.Port);
            Assert.AreEqual(WorkerListenPort.Source.Environment, r.From);
            StringAssert.Contains("-nebula-port", r.Warning);
        }

        [Test]
        public void TheHighestAndLowestPortsAreAccepted()
        {
            Assert.AreEqual(65535, WorkerListenPort.Resolve(null, "65535", 7100, 3).Port);
            Assert.AreEqual(1, WorkerListenPort.Resolve(null, " 1 ", 7100, 3).Port);
        }
    }
}
