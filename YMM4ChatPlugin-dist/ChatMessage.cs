using System;

namespace YMM4ChatPlugin
{
    public enum ChatMessageType
    {
        Normal,
        System,
        JoinLeave
    }

    public class ChatMessage
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public ChatMessageType Type { get; set; }
        public string UserName { get; set; } = "";
        public string Text { get; set; } = "";
        public string? ReplyToId { get; set; }
        public string? ReplyPreview { get; set; }
        public string RoomId { get; set; } = "";

        public string DisplayText
        {
            get
            {
                if (Type == ChatMessageType.Normal)
                    return $"{UserName}: {Text}";
                else if (Type == ChatMessageType.JoinLeave)
                    return Text;
                else
                    return $"[システム] {Text}";
            }
        }
    }
}