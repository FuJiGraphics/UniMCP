namespace UniMCP.Editor.Chat
{
    public enum eChatRole
    {
        User,
        Assistant,
        System,
    }

    [System.Serializable]
    public class ChatMessage
    {
        public eChatRole role;
        public string text;
    }
}
