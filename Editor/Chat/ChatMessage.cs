using System;

namespace UniMCP.Editor.Chat
{
    public enum eChatRole
    {
        User,
        Assistant,
        System,
    }

    public class ChatMessage
    {
        public eChatRole role;
        public string text;
        public DateTime timestamp;
    }
}
