using System;
using YukkuriMovieMaker.Plugin;

namespace YMM4ChatPlugin
{
    public class ChatPlugin : IToolPlugin
    {
        public string Name => "YMM4チャット";
        public Type ViewType => typeof(ChatView);
        public Type ViewModelType => typeof(ChatViewModel);

        public void Show() { }
    }
}