using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Agent.Core.Models;
using VibeCoder.Core.Services;

namespace VibeCoder.Tests
{
    [TestFixture]
    public class TranscriptBuilderTests
    {
        [Test]
        public void Build_Null_ReturnsEmpty()
        {
            Assert.That(TranscriptBuilder.Build(null), Is.Empty);
        }

        [Test]
        public void Build_UserMessage_MapsToUserRole()
        {
            var result = TranscriptBuilder.Build(new[]
            {
                new ChatMessage { Kind = ChatMessageKind.User, Text = "hello" }
            });

            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(result[0].Role, Is.EqualTo("user"));
            Assert.That(result[0].Content, Is.EqualTo("hello"));
        }

        [Test]
        public void Build_AnswerKinds_MapToAssistantRole()
        {
            var messages = new List<ChatMessage>
            {
                new ChatMessage { Kind = ChatMessageKind.AgentText, Text = "a" },
                new ChatMessage { Kind = ChatMessageKind.ResultAnswerPlan, Text = "b" },
                new ChatMessage { Kind = ChatMessageKind.ResultAnswerAct, Text = "c" },
            };

            var result = TranscriptBuilder.Build(messages);

            Assert.That(result, Has.Count.EqualTo(3));
            Assert.That(result.All(m => m.Role == "assistant"), Is.True);
            Assert.That(result.Select(m => m.Content).ToArray(), Is.EqualTo(new[] { "a", "b", "c" }));
        }

        [Test]
        public void Build_SkipsWhitespaceAndNullMessages()
        {
            var messages = new List<ChatMessage>
            {
                null,
                new ChatMessage { Kind = ChatMessageKind.User, Text = "   " },
                new ChatMessage { Kind = ChatMessageKind.User, Text = "" },
                new ChatMessage { Kind = ChatMessageKind.User, Text = "kept" },
            };

            var result = TranscriptBuilder.Build(messages);

            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(result[0].Content, Is.EqualTo("kept"));
        }

        [Test]
        public void Build_NonTranscriptKinds_AreSkipped()
        {
            var messages = new List<ChatMessage>
            {
                new ChatMessage { Kind = ChatMessageKind.ToolCall, Text = "tool" },
                new ChatMessage { Kind = ChatMessageKind.ToolBlocked, Text = "blocked" },
                new ChatMessage { Kind = ChatMessageKind.Error, Text = "boom" },
                new ChatMessage { Kind = ChatMessageKind.SystemNotice, Text = "note" },
                new ChatMessage { Kind = ChatMessageKind.Compaction, Text = "compact" },
                new ChatMessage { Kind = ChatMessageKind.ModeSwitch, Text = "switch" },
                new ChatMessage { Kind = ChatMessageKind.DiffEdit, Text = "diff" },
                new ChatMessage { Kind = ChatMessageKind.Question, Text = "question?" },
                new ChatMessage { Kind = ChatMessageKind.Approval, Text = "approve?" },
                new ChatMessage { Kind = ChatMessageKind.Subagent, Text = "sub" },
                new ChatMessage { Kind = ChatMessageKind.Checkpoint, Text = "checkpoint" },
                new ChatMessage { Kind = ChatMessageKind.Diff, Text = "diff2" },
            };

            Assert.That(TranscriptBuilder.Build(messages), Is.Empty);
        }
    }
}
