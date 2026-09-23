using System.Collections.Generic;
using NUnit.Framework;

namespace Nebula.Tests
{
    public sealed class CommandLineTests
    {
        private static Dictionary<string, string> Parse(params string[] args)
        {
            var into = new Dictionary<string, string>();
            CommandLine.ParseArgs(args, 0, into);
            return into;
        }

        [Test]
        public void KeyAndValueAsTwoArguments()
        {
            var a = Parse("-nebula-scope", "planet/1/0", "-batchmode");
            Assert.AreEqual("planet/1/0", a["nebula-scope"]);
            Assert.AreEqual("", a["batchmode"]);
        }

        [Test]
        public void KeyEqualsValueIsOneArgument()
        {
            var a = Parse("-nebula-probe=false", "--port=7400");
            Assert.AreEqual("false", a["nebula-probe"]);
            Assert.AreEqual("7400", a["port"]);
            Assert.IsFalse(a.ContainsKey("nebula-probe=false"));
        }

        [Test]
        public void OnlyTheEqualsSpellingCarriesAValueThatStartsWithADash()
        {
            var a = Parse("-heading=-30", "-tilt", "-5");
            Assert.AreEqual("-30", a["heading"]);
            Assert.AreEqual("", a["tilt"], "a dash starts the next switch, as before");
            Assert.AreEqual("", a["5"]);
        }

        [Test]
        public void AnEmptyValueAfterEqualsAndBareWordsAreHandled()
        {
            var a = Parse("stray", "-name=", "-=x");
            Assert.AreEqual("", a["name"]);
            Assert.AreEqual(1, a.Count, "a bare word and an empty key are ignored");
        }
    }
}
