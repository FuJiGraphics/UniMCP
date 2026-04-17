using System;
using System.Collections.Generic;
using UnityEngine;

namespace UniMCP.Editor.Chat
{
    [Serializable]
    public class ChatSessionData
    {
        public string name;
        public List<ChatMessage> messages = new();
        public string input = "";
        public Vector2 scroll;
        public string claudeSessionId;

        [NonSerialized] public bool isWaiting;
        [NonSerialized] public bool scrollToBottom;
        [NonSerialized] public double thinkingStartedAt;
        [NonSerialized] public int thinkingDots;
    }
}
