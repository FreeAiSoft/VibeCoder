using Newtonsoft.Json.Linq;
using NUnit.Framework;
using VibeCoder.Core.Services;

namespace VibeCoder.Tests
{
    [TestFixture]
    public class ToolCallDisplayFormatterTests
    {
        [Test]
        public void Format_ListFiles_IncludesPathAndResult()
        {
            var args = JObject.Parse("{ \"path\": \"src\", \"recursive\": true }");
            var result = "A/\nB.cs\nC.cs\nD.cs\nE.cs\nF.cs\nG.cs";

            var text = ToolCallDisplayFormatter.Format("list_files", args, result, "");

            Assert.That(text, Is.EqualTo("list_files → src (recursive)\n" + result));
        }

        [Test]
        public void Format_ListFiles_NonRecursive_OmitsFlag()
        {
            var args = JObject.Parse("{ \"path\": \".\", \"recursive\": false }");
            var text = ToolCallDisplayFormatter.Format("list_files", args, "a.cs", "");
            Assert.That(text, Is.EqualTo("list_files → .\na.cs"));
        }

        [Test]
        public void Format_SearchCodebase_IncludesQuotedQueryAndResult()
        {
            var args = JObject.Parse("{ \"queries\": [\"Studio.WindowBackground\"] }");
            var text = ToolCallDisplayFormatter.Format("search_codebase", args, "App.xaml:5: Background=\"...\"", "");
            Assert.That(text, Is.EqualTo("search_codebase → \"Studio.WindowBackground\"\nApp.xaml:5: Background=\"...\""));
        }

        [Test]
        public void Format_SearchCodebase_JoinsMultipleQueries()
        {
            var args = JObject.Parse("{ \"queries\": [\"Foo\", \"Bar\"] }");
            var text = ToolCallDisplayFormatter.Format("search_codebase", args, "x", "");
            Assert.That(text, Is.EqualTo("search_codebase → \"Foo\", \"Bar\"\nx"));
        }

        [Test]
        public void Format_ReadFiles_ListsFilesOnePerLine()
        {
            var text = ToolCallDisplayFormatter.Format("read_files", new JObject(), "", "C:\\a.xaml\nC:\\b.cs");
            Assert.That(text, Is.EqualTo("read_files:\nC:\\a.xaml\nC:\\b.cs"));
        }

        [Test]
        public void Format_Default_UsesDisplayResultWhenPresent()
        {
            var text = ToolCallDisplayFormatter.Format("editor", new JObject(), "full", "short");
            Assert.That(text, Is.EqualTo("editor → short"));
        }

        [Test]
        public void Format_Default_FallsBackToResult()
        {
            var text = ToolCallDisplayFormatter.Format("build", new JObject(), "build output", "");
            Assert.That(text, Is.EqualTo("build → build output"));
        }

        [Test]
        public void Collapse_TruncatesToMaxLines()
        {
            var full = "header\n1\n2\n3\n4\n5\n6\n7";
            Assert.That(ToolCallDisplayFormatter.Collapse(full, 6), Is.EqualTo("header\n1\n2\n3\n4\n5"));
        }

        [Test]
        public void Collapse_ReturnsFullTextWhenWithinLimit()
        {
            var full = "header\n1\n2";
            Assert.That(ToolCallDisplayFormatter.Collapse(full, 6), Is.EqualTo(full));
        }

        [Test]
        public void HasMore_DetectsExtraLines()
        {
            Assert.That(ToolCallDisplayFormatter.HasMore("a\nb\nc", 2), Is.True);
            Assert.That(ToolCallDisplayFormatter.HasMore("a\nb", 2), Is.False);
            Assert.That(ToolCallDisplayFormatter.HasMore("", 2), Is.False);
            Assert.That(ToolCallDisplayFormatter.HasMore(null, 2), Is.False);
        }
    }
}
