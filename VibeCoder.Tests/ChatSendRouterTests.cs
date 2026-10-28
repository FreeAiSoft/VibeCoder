using NUnit.Framework;
using VibeCoder.Core.Services;

namespace VibeCoder.Tests
{
    [TestFixture]
    public class ChatSendRouterTests
    {
        [Test]
        public void Decide_AgentFree_Sends_RegardlessOfQuestionAndText()
        {
            Assert.That(ChatSendRouter.Decide(false, false, false), Is.EqualTo(ChatSendAction.Send));
            Assert.That(ChatSendRouter.Decide(false, false, true), Is.EqualTo(ChatSendAction.Send));
            Assert.That(ChatSendRouter.Decide(false, true, false), Is.EqualTo(ChatSendAction.Send));
            Assert.That(ChatSendRouter.Decide(false, true, true), Is.EqualTo(ChatSendAction.Send));
        }

        [Test]
        public void Decide_BusyWithActiveQuestionAndText_AnswersQuestion()
        {
            Assert.That(ChatSendRouter.Decide(true, true, true), Is.EqualTo(ChatSendAction.AnswerQuestion));
        }

        [Test]
        public void Decide_BusyWithActiveQuestionButNoText_Drops()
        {
            Assert.That(ChatSendRouter.Decide(true, true, false), Is.EqualTo(ChatSendAction.Drop));
        }

        [Test]
        public void Decide_BusyWithoutActiveQuestion_Drops()
        {
            Assert.That(ChatSendRouter.Decide(true, false, true), Is.EqualTo(ChatSendAction.Drop));
            Assert.That(ChatSendRouter.Decide(true, false, false), Is.EqualTo(ChatSendAction.Drop));
        }
    }
}
