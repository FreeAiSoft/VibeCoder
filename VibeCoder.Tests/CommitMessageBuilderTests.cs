using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using VibeCoder.Core.Services;

namespace VibeCoder.Tests
{
    [TestFixture]
    public class CommitMessageBuilderTests
    {
        // ---------- SanitizeSummary / Format*Line ----------

        [Test]
        public void FormatProjectLine_StripsTypePrefix()
        {
            Assert.That(
                CommitMessageBuilder.FormatProjectLine("VibeCoder.Core", "fix: fix parser"),
                Is.EqualTo("VibeCoder.Core: fix parser"));
        }

        [Test]
        public void FormatChoreLine_PrefixesChoreOnce()
        {
            Assert.That(
                CommitMessageBuilder.FormatChoreLine("chore: tidy up"),
                Is.EqualTo("chore: tidy up"));
        }

        [Test]
        public void SanitizeSummary_StripsCodeFencesAndQuotes()
        {
            Assert.That(
                CommitMessageBuilder.SanitizeSummary("```\nadd validation\n```"),
                Is.EqualTo("add validation"));
            Assert.That(
                CommitMessageBuilder.SanitizeSummary("`MyProject: add validation`"),
                Is.EqualTo("add validation"));
            Assert.That(
                CommitMessageBuilder.SanitizeSummary("\"MyProject: add validation\""),
                Is.EqualTo("add validation"));
        }

        [Test]
        public void SanitizeSummary_LeavesPlainSummaryIntact()
        {
            Assert.That(CommitMessageBuilder.SanitizeSummary("add validation"), Is.EqualTo("add validation"));
        }

        // ---------- GroupChangedFiles ----------

        [Test]
        public void GroupChangedFiles_GroupsOrdinaryProjectsByName()
        {
            var root = CreateTempRoot();
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "VibeCoder.Core", "Services"));
                Directory.CreateDirectory(Path.Combine(root, "VibeCoder"));
                File.WriteAllText(Path.Combine(root, "VibeCoder.Core", "VibeCoder.Core.csproj"), "");
                File.WriteAllText(Path.Combine(root, "VibeCoder", "VibeCoder.csproj"), "");

                var plan = CommitMessageBuilder.GroupChangedFiles(root, new[]
                {
                    "VibeCoder.Core/Services/A.cs",
                    "VibeCoder/B.cs",
                });

                Assert.That(plan.Projects.Select(p => p.Name), Is.EquivalentTo(new[] { "VibeCoder.Core", "VibeCoder" }));
            }
            finally
            {
                DeleteTempRoot(root);
            }
        }

        [Test]
        public void GroupChangedFiles_PutsRootFilesIntoOther()
        {
            var root = CreateTempRoot();
            try
            {
                var plan = CommitMessageBuilder.GroupChangedFiles(root, new[] { ".gitignore", "memory-bank/x.md" });

                Assert.That(plan.OtherFiles.Count, Is.EqualTo(2));
                Assert.That(plan.Projects.Count, Is.EqualTo(0));
            }
            finally
            {
                DeleteTempRoot(root);
            }
        }

        private static string CreateTempRoot()
        {
            var root = Path.Combine(Path.GetTempPath(), "vibecoder_commit_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return root;
        }

        private static void DeleteTempRoot(string root)
        {
            try { Directory.Delete(root, true); }
            catch { /* best effort cleanup */ }
        }
    }
}
