using System;
using NUnit.Framework;
using VibeCoder.Core.Models;
using VibeCoder.Core.Services;

namespace VibeCoder.Tests
{
    [TestFixture]
    public class ChatTimeFormatterTests
    {
        [Test]
        public void FormatLocalTime_RoundTripsToLocalHoursMinutesSeconds()
        {
            var local = new DateTime(2024, 3, 5, 9, 5, 7, DateTimeKind.Local);
            var utc = local.ToUniversalTime();
            Assert.AreEqual("09:05:07", ChatTimeFormatter.FormatLocalTime(utc));
        }

        [Test]
        public void FormatLocalTime_PadsSingleDigits()
        {
            var local = new DateTime(2024, 3, 5, 1, 2, 3, DateTimeKind.Local);
            var utc = local.ToUniversalTime();
            Assert.AreEqual("01:02:03", ChatTimeFormatter.FormatLocalTime(utc));
        }
    }

    [TestFixture]
    public class ChatMessageControlIdTests
    {
        [Test]
        public void ForKind_ReturnsExpectedIdForEveryKind()
        {
            var expected = new (ChatMessageKind Kind, string Id)[]
            {
                (ChatMessageKind.User, "User"),
                (ChatMessageKind.AgentText, "AgentText"),
                (ChatMessageKind.ResultAnswerPlan, "ResultAnswerPlan"),
                (ChatMessageKind.ResultAnswerAct, "ResultAnswerAct"),
                (ChatMessageKind.ToolCall, "ToolCall"),
                (ChatMessageKind.ToolBlocked, "ToolBlocked"),
                (ChatMessageKind.Error, "Error"),
                (ChatMessageKind.SystemNotice, "SystemNotice"),
                (ChatMessageKind.Compaction, "Compaction"),
                (ChatMessageKind.ModeSwitch, "ModeSwitch"),
                (ChatMessageKind.DiffEdit, "DiffEdit"),
                (ChatMessageKind.Question, "Question"),
                (ChatMessageKind.Subagent, "Subagent"),
                (ChatMessageKind.Checkpoint, "Checkpoint"),
            };

            foreach (var (kind, id) in expected)
            {
                Assert.AreEqual(id, ChatMessageControlId.ForKind(kind));
            }
        }

        [Test]
        public void ForKind_CoversAllEnumValuesWithoutFallback()
        {
            foreach (ChatMessageKind kind in Enum.GetValues(typeof(ChatMessageKind)))
            {
                var id = ChatMessageControlId.ForKind(kind);
                Assert.IsNotNull(id);
                Assert.IsNotEmpty(id);
            }
        }
    }
}