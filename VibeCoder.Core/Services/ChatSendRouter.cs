namespace VibeCoder.Core.Services
{
    /// <summary>
    /// What to do with user input typed into the chat box while the agent may still be busy.
    /// </summary>
    public enum ChatSendAction
    {
        /// <summary>The agent is busy with no active question; the input is not sent.</summary>
        Drop,

        /// <summary>The agent is free; send the input as a normal chat message.</summary>
        Send,

        /// <summary>The agent is awaiting a question answer; the input becomes the answer.</summary>
        AnswerQuestion
    }

    /// <summary>
    /// Pure routing decision for chat input. Kept in Core (no WPF dependency) so the
    /// "ignore the question and just type a message" behaviour can be unit-tested.
    /// </summary>
    public static class ChatSendRouter
    {
        /// <summary>
        /// Decides how to handle the typed input.
        /// </summary>
        /// <param name="agentBusy">True while a previous agent turn is still running.</param>
        /// <param name="hasActiveQuestion">True when the agent is currently awaiting an ask_question answer.</param>
        /// <param name="hasText">True when the input box contains non-empty text.</param>
        public static ChatSendAction Decide(bool agentBusy, bool hasActiveQuestion, bool hasText)
        {
            if (!agentBusy)
                return ChatSendAction.Send;

            if (hasActiveQuestion && hasText)
                return ChatSendAction.AnswerQuestion;

            return ChatSendAction.Drop;
        }
    }
}
