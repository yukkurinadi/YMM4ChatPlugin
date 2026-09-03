using System;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace YMM4ChatPlugin
{
    public partial class ChatView : UserControl
    {
        private bool _termsClicked = false;
        private bool _privacyClicked = false;
        private bool _eventsRegistered = false;

        public ChatView()
        {
            InitializeComponent();
            this.Loaded   += OnLoaded;
            this.Unloaded += OnUnloaded;

            // YMM4 が自動的に DataContext を設定するので、ここでは何もしない
            if (DataContext is ChatViewModel vm)
            {
                vm.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(vm.IsAgreed))
                        UpdateAgreementVisibility(vm.IsAgreed);
                    if (args.PropertyName == nameof(vm.CurrentRoomName))
                    {
                        var window = Window.GetWindow(this);
                        if (window != null)
                            window.Title = $"YMM4チャット - ルーム: {vm.CurrentRoomName}";
                    }
                };
                UpdateAgreementVisibility(vm.IsAgreed);
                var window2 = Window.GetWindow(this);
                if (window2 != null && !string.IsNullOrEmpty(vm.CurrentRoomName))
                    window2.Title = $"YMM4チャット - ルーム: {vm.CurrentRoomName}";
            }
        }

        private void UpdateAgreementVisibility(bool isAgreed)
        {
            AgreementBorder.Visibility = isAgreed ? Visibility.Collapsed : Visibility.Visible;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_eventsRegistered) return;
            _eventsRegistered = true;

            // テーマ文字色を初期化し、テーマ変更をリアクティブに追跡
            ThemeColors.UpdateTextColors();
            if (Application.Current?.Resources?.MergedDictionaries is INotifyCollectionChanged nc)
            {
                nc.CollectionChanged += (_, _) =>
                    Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)ThemeColors.UpdateTextColors);
            }

            TermsButton.Click += (s, ev) =>
            {
                _termsClicked = true;
                ShowMarkdownWindow("利用規約", "YMM4ChatPlugin.利用規約.md");
                UpdateAgreeCheckBox();
            };
            PrivacyButton.Click += (s, ev) =>
            {
                _privacyClicked = true;
                ShowMarkdownWindow("プライバシーポリシー", "YMM4ChatPlugin.プライバシーポリシー.md");
                UpdateAgreeCheckBox();
            };

            AgreeCheckBox.Checked   += (s, ev) => SetAgreed(true);
            AgreeCheckBox.Unchecked += (s, ev) => SetAgreed(false);

            this.Focus();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (DataContext is ChatViewModel vm)
            {
                vm.Dispose();
                // DataContext = null;  // YMM4が管理するのでやらない
            }
        }

        // ── その他ハンドラ ──────────────────────────────────────────────────

        private void ShowMarkdownWindow(string title, string resourceName)
        {
            var markdown = MarkdownViewerWindow.LoadEmbeddedMarkdown(resourceName);
            var win = new MarkdownViewerWindow(title, markdown)
            {
                Owner = Window.GetWindow(this),
            };
            win.ShowDialog();
        }

        private void UpdateAgreeCheckBox()
        {
            AgreeCheckBox.IsEnabled = _termsClicked && _privacyClicked;
        }

        private void SetAgreed(bool agreed)
        {
            if (DataContext is ChatViewModel vm)
            {
                vm.IsAgreed = agreed;
            }
        }
    }
}