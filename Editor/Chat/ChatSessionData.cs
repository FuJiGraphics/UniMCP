using System.Collections.Generic;
using UnityEngine;

namespace UniMCP.Editor.Chat
{
    public class ChatSessionData
    {
        public string name;
        public readonly List<ChatMessage> messages = new();
        public string input = "";
        public Vector2 scroll;
        public bool isWaiting;
        public bool scrollToBottom;
        public double thinkingStartedAt;
        public int thinkingDots;
    }
}
