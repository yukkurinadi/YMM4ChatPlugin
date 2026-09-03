using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace YMM4ChatPlugin
{
    public class ChatViewModel : INotifyPropertyChanged, IDisposable
    {
        private SupabaseService? _supabase;
        private bool _isHostMode;
        private string _messageText = "";
        private string _connectionInfo = "";
        private string _roomIdInput = "";
        private string _userName = "匿名";
        private string _currentRoomId = "";
        private string _currentRoomName = "";
        private ObservableCollection<ChatMessage> _messages = new();
        private ObservableCollection<RoomInfo> _rooms = new();
        private bool _isAgreed;
        private RoomInfo? _selectedRoom;
        private string _roomSearchText = "";
        private ObservableCollection<RoomInfo> _filteredRooms = new();
        private bool _isSwitchingRoom;
        private bool _isLoadingRooms;
        private readonly SemaphoreSlim _switchLock = new(1, 1);
        private DispatcherTimer? _selectionDelayTimer;
        private string? _delayedSelectedRoomId;
        private bool _isDisposed = false;
        private int _retryCount = 0;
        private const int MaxRetries = 3;

        private string? _lastMessageText;
        private DateTime _lastMessageTime;
        private readonly TimeSpan _duplicateInterval = TimeSpan.FromSeconds(30);
        private readonly SafeBrowsingService _safeBrowsing = new();

        private bool _userNameLoaded = false;

        public ObservableCollection<ChatMessage> Messages
        {
            get => _messages;
            set { _messages = value; OnPropertyChanged(); }
        }

        public ObservableCollection<RoomInfo> Rooms
        {
            get => _rooms;
            set { _rooms = value; OnPropertyChanged(); }
        }

        public ObservableCollection<RoomInfo> FilteredRooms
        {
            get => _filteredRooms;
            set { _filteredRooms = value; OnPropertyChanged(); }
        }

        public bool IsRoomSwitching => _isSwitchingRoom;
        public bool IsListBoxEnabled => !_isSwitchingRoom && !_isLoadingRooms;

        public RoomInfo? SelectedRoom
        {
            get => _selectedRoom;
            set
            {
                if (value == null) return;
                if (_isSwitchingRoom || _isLoadingRooms) return;
                if (_selectedRoom?.Id == value.Id) return;

                if (_selectionDelayTimer == null)
                {
                    _selectionDelayTimer = new DispatcherTimer
                    {
                        Interval = TimeSpan.FromMilliseconds(200),
                        IsEnabled = false
                    };
                    _selectionDelayTimer.Tick += OnSelectionDelayTimerTick;
                }
                _delayedSelectedRoomId = value.Id;
                _selectionDelayTimer.Stop();
                _selectionDelayTimer.Start();
            }
        }

        private async void OnSelectionDelayTimerTick(object? sender, EventArgs e)
        {
            if (_isDisposed) return;
            _selectionDelayTimer?.Stop();
            var targetId = _delayedSelectedRoomId;
            _delayedSelectedRoomId = null;
            if (string.IsNullOrEmpty(targetId)) return;
            await ExecuteRoomSelection(targetId);
        }

        private async Task ExecuteRoomSelection(string roomId)
        {
            if (_isDisposed) return;
            var targetRoom = Rooms.FirstOrDefault(r => r.Id == roomId);
            if (targetRoom == null) return;

            if (_selectedRoom?.Id != roomId)
            {
                _selectedRoom = targetRoom;
                OnPropertyChanged(nameof(SelectedRoom));
            }

            if (roomId != CurrentRoomId)
            {
                await RequestSwitchRoomAsync(roomId);
            }
        }

        public string RoomSearchText
        {
            get => _roomSearchText;
            set
            {
                _roomSearchText = value;
                OnPropertyChanged();
                ApplyRoomFilter();
            }
        }

        public string MessageText
        {
            get => _messageText;
            set { _messageText = value; OnPropertyChanged(); }
        }

        public string ConnectionInfo
        {
            get => _connectionInfo;
            set { _connectionInfo = value; OnPropertyChanged(); }
        }

        public string RoomIdInput
        {
            get => _roomIdInput;
            set { _roomIdInput = value; OnPropertyChanged(); }
        }

        public string UserName
        {
            get => _userName;
            set
            {
                if (_userName != value)
                {
                    _userName = value;
                    OnPropertyChanged();
                    if (_userNameLoaded)
                        AgreementSettings.SaveAgreement(IsAgreed, _userName);
                }
            }
        }

        public string CurrentRoomId
        {
            get => _currentRoomId;
            set
            {
                if (_currentRoomId != value)
                {
                    _currentRoomId = value;
                    OnPropertyChanged();
                }
            }
        }

        public string CurrentRoomName
        {
            get => _currentRoomName;
            set
            {
                if (_currentRoomName != value)
                {
                    _currentRoomName = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsHostMode
        {
            get => _isHostMode;
            set { _isHostMode = value; OnPropertyChanged(); }
        }

        public bool IsAgreed
        {
            get => _isAgreed;
            set
            {
                if (_isAgreed != value)
                {
                    _isAgreed = value;
                    OnPropertyChanged();
                    if (_userNameLoaded)
                        AgreementSettings.SaveAgreement(_isAgreed, _userName);
                    if (value && _supabase == null)
                    {
                        _ = InitializeAfterAgreement();
                    }
                }
            }
        }

        public ICommand CreateRoomCommand { get; }
        public ICommand SendMessageCommand { get; }
        public ICommand CopyRoomIdCommand { get; }
        public ICommand ShowCreateRoomDialogCommand { get; }
        public ICommand RetryConnectionCommand { get; } // 追加：再試行コマンド

        public ChatViewModel()
        {
            var (savedAgreed, savedUserName) = AgreementSettings.LoadAgreement();
            _isAgreed = savedAgreed;
            _userName = savedUserName;
            _userNameLoaded = true;

            if (_isAgreed && _supabase == null)
            {
                _ = InitializeAfterAgreement();
            }

            ShowCreateRoomDialogCommand = new RelayCommand(_ => ShowCreateRoomDialog(), _ => IsAgreed);
            CreateRoomCommand = new RelayCommand(_ => _ = CreateRoomWithNameAsync(null), _ => IsAgreed);
            SendMessageCommand = new RelayCommand(_ => SendMessage(), _ => IsAgreed && !string.IsNullOrWhiteSpace(MessageText) && _supabase?.IsConnected == true && !_isSwitchingRoom);
            CopyRoomIdCommand = new RelayCommand(_ => CopyRoomId(), _ => IsAgreed && !string.IsNullOrWhiteSpace(CurrentRoomId));
            RetryConnectionCommand = new RelayCommand(_ => _ = RetryConnectionAsync());
        }

        private async Task RetryConnectionAsync()
        {
            _retryCount = 0;
            AddSystemMessage("再接続を試みます...");
            await InitializeAfterAgreement();
        }

        private void ApplyRoomFilter()
        {
            if (_isSwitchingRoom) return;

            var source = Rooms.OrderBy(r => !r.IsPinned).ThenBy(r => r.Name);

            var filtered = string.IsNullOrWhiteSpace(RoomSearchText)
                ? source.ToList()
                : source.Where(r => r.Name.Contains(RoomSearchText, StringComparison.OrdinalIgnoreCase)).ToList();

            var currentSelectedId = _selectedRoom?.Id;

            FilteredRooms = new ObservableCollection<RoomInfo>(filtered);
            OnPropertyChanged(nameof(FilteredRooms));

            if (!string.IsNullOrEmpty(currentSelectedId))
            {
                var roomToSelect = FilteredRooms.FirstOrDefault(r => r.Id == currentSelectedId);
                if (roomToSelect != null && _selectedRoom?.Id != roomToSelect.Id)
                {
                    _selectedRoom = roomToSelect;
                    OnPropertyChanged(nameof(SelectedRoom));
                }
            }
        }

        private async Task InitializeAfterAgreement()
        {
            if (_supabase != null)
            {
                // 既に接続済みなら再接続しない
                if (_supabase.IsConnected)
                {
                    AddSystemMessage("既に接続されています。");
                    return;
                }
                _supabase.Dispose();
                _supabase = null;
            }

            AddSystemMessage("チャット初期化中...");

            _supabase = new SupabaseService();
            _supabase.SetCurrentUserName(UserName);
            _supabase.OnConnected += async (msg) =>
            {
                Application.Current.Dispatcher.Invoke((Action)(() => AddSystemMessage("Supabaseに接続しました")));
                _retryCount = 0;
                await LoadRoomsAsync();
            };
            _supabase.OnMessageReceived += (msg) =>
            {
                Application.Current.Dispatcher.Invoke((Action)(() => AddRemoteMessage(msg)));
            };
            _supabase.OnError += (err) =>
            {
                Application.Current.Dispatcher.Invoke((Action)(() =>
                {
                    AddSystemMessage($"Supabaseエラー: {err}");
                    // エラーがキャンセル関連なら自動再試行（ただしループ防止のため制限回数まで）
                    if (err.Contains("canceled") && _retryCount < MaxRetries)
                    {
                        _retryCount++;
                        AddSystemMessage($"自動再接続を試みます（{_retryCount}/{MaxRetries}）...");
                        _ = Task.Delay(2000).ContinueWith(_ => InitializeAfterAgreement());
                    }
                }));
            };
            _supabase.OnRoomChanged += async (roomId) =>
            {
                CurrentRoomId = roomId;
                var room = Rooms.FirstOrDefault(r => r.Id == roomId);
                CurrentRoomName = room?.Name ?? "不明";
                await LoadMessagesForRoom(roomId);
            };
            await _supabase.InitializeAsync();
        }

        private async Task LoadRoomsAsync()
        {
            if (_supabase == null) return;
            _isLoadingRooms = true;
            OnPropertyChanged(nameof(IsListBoxEnabled));
            AddSystemMessage("ルーム一覧を読み込み中...");

            try
            {
                var rooms = await _supabase.LoadRoomsAsync();
                if (rooms == null)
                {
                    AddSystemMessage("ルーム一覧が null で返されました。");
                    return;
                }

                AddSystemMessage($"Supabaseから{rooms.Count}件のルームを受信しました。");

                Application.Current.Dispatcher.Invoke((Action)(() =>
                {
                    Rooms.Clear();
                    foreach (var r in rooms) Rooms.Add(r);
                    ApplyRoomFilter();

                    if (FilteredRooms.Count > 0 && _selectedRoom == null)
                    {
                        _selectedRoom = FilteredRooms[0];
                        OnPropertyChanged(nameof(SelectedRoom));
                        CurrentRoomName = _selectedRoom.Name;
                        AddSystemMessage($"現在のルーム: {CurrentRoomName}");
                    }
                    else if (FilteredRooms.Count == 0)
                    {
                        AddSystemMessage("ルームが見つかりません。ルームを作成してください。");
                    }
                }));
            }
            catch (Exception ex)
            {
                AddSystemMessage($"ルーム読み込みエラー: {ex.Message}");
                if (_retryCount < MaxRetries)
                {
                    _retryCount++;
                    AddSystemMessage($"自動再試行（{_retryCount}/{MaxRetries}）...");
                    await Task.Delay(2000);
                    await LoadRoomsAsync();
                }
            }
            finally
            {
                _isLoadingRooms = false;
                OnPropertyChanged(nameof(IsListBoxEnabled));
            }
        }

        private async Task LoadMessagesForRoom(string roomId)
        {
            if (_supabase == null) return;
            var history = await _supabase.LoadHistoryAsync();
            Application.Current.Dispatcher.Invoke((Action)(() =>
            {
                Messages.Clear();
                foreach (var msg in history) Messages.Add(msg);
            }));
        }

        private async Task RequestSwitchRoomAsync(string roomId)
        {
            if (_isDisposed) return;
            await _switchLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_isDisposed) return;
                await PerformSwitchRoomAsync(roomId);
            }
            finally
            {
                if (!_isDisposed) _switchLock.Release();
            }
        }

        private async Task PerformSwitchRoomAsync(string roomId)
        {
            if (_supabase == null || _isDisposed) return;

            _isSwitchingRoom = true;
            OnPropertyChanged(nameof(IsRoomSwitching));
            OnPropertyChanged(nameof(IsListBoxEnabled));

            var sw = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                var success = await _supabase.JoinRoomAsync(roomId);
                sw.Stop();

                if (success)
                {
                    CurrentRoomId = roomId;
                    var room = Rooms.FirstOrDefault(r => r.Id == roomId);
                    CurrentRoomName = room?.Name ?? "不明";

                    if (_selectedRoom?.Id != roomId)
                    {
                        _selectedRoom = room;
                        OnPropertyChanged(nameof(SelectedRoom));
                    }

                    Application.Current.Dispatcher.Invoke((Action)(() => Messages.Clear()));
                    await LoadMessagesForRoom(roomId);
                    AddSystemMessage($"ルーム「{CurrentRoomName}」に切り替えました（{sw.ElapsedMilliseconds}ms）");
                }
                else
                {
                    AddSystemMessage("ルーム切り替えに失敗しました。");
                    var originalRoom = Rooms.FirstOrDefault(r => r.Id == CurrentRoomId);
                    if (originalRoom != null && _selectedRoom?.Id != CurrentRoomId)
                        _selectedRoom = originalRoom;
                    OnPropertyChanged(nameof(SelectedRoom));
                }
            }
            catch (Exception ex)
            {
                AddSystemMessage($"ルーム切り替えエラー: {ex.Message}");
            }
            finally
            {
                _isSwitchingRoom = false;
                OnPropertyChanged(nameof(IsRoomSwitching));
                OnPropertyChanged(nameof(IsListBoxEnabled));
            }
        }

        private void ShowCreateRoomDialog()
        {
            var owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current.MainWindow;
            if (owner == null) return;

            var dialog = new Window
            {
                Title = "新しいルームを作成",
                Width = 400,
                Height = 150,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = owner,
                ShowInTaskbar = false,
                ResizeMode = ResizeMode.NoResize
            };
            var nameBox = new TextBox { Margin = new Thickness(0, 0, 0, 10) };
            var okButton = new Button { Content = "作成", Width = 75, Margin = new Thickness(0, 0, 5, 0), IsDefault = true };
            var cancelButton = new Button { Content = "キャンセル", Width = 75, IsCancel = true };
            var panel = new StackPanel { Margin = new Thickness(10) };
            panel.Children.Add(new TextBlock { Text = "ルーム名:", Margin = new Thickness(0, 0, 0, 5) });
            panel.Children.Add(nameBox);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(okButton);
            buttons.Children.Add(cancelButton);
            panel.Children.Add(buttons);
            dialog.Content = panel;

            okButton.Click += async (s, e) =>
            {
                var roomName = nameBox.Text.Trim();
                if (string.IsNullOrWhiteSpace(roomName)) roomName = "新しいルーム";
                dialog.Close();
                await CreateRoomWithNameAsync(roomName);
            };
            cancelButton.Click += (s, e) => dialog.Close();

            dialog.ShowDialog();
        }

        private async Task CreateRoomWithNameAsync(string? roomName)
        {
            if (_supabase == null || _isDisposed) return;
            if (string.IsNullOrWhiteSpace(roomName)) roomName = "新しいルーム";
            var roomId = await _supabase.CreateRoomAsync(roomName);
            if (!string.IsNullOrEmpty(roomId))
            {
                AddSystemMessage($"ルーム「{roomName}」を作成しました");
                await LoadRoomsAsync();
                var newRoom = Rooms.FirstOrDefault(r => r.Id == roomId);
                if (newRoom != null) await RequestSwitchRoomAsync(newRoom.Id);
            }
        }

        private async void SendMessage()
        {
            if (!IsAgreed || string.IsNullOrWhiteSpace(MessageText) || _supabase == null || !_supabase.IsConnected) return;
            if (_isSwitchingRoom)
            {
                AddSystemMessage("ルーム切り替え中です。しばらくお待ちください。");
                return;
            }
            if (string.IsNullOrEmpty(CurrentRoomId))
            {
                AddSystemMessage("現在のルームIDが不明です。ルームを選択してください。");
                return;
            }

            string text = MessageText.Trim();

            if (text.Length > 140)
            {
                AddSystemMessage("メッセージは140文字以内で入力してください。");
                return;
            }

            if (_lastMessageText != null && text == _lastMessageText && DateTime.Now - _lastMessageTime < _duplicateInterval)
            {
                AddSystemMessage("同じ内容のメッセージを連続して送信できません。しばらく待ってから再送信してください。");
                return;
            }

            var urls = ExtractUrls(text);
            if (urls.Count > 0)
            {
                var previousUrls = _lastMessageText != null ? ExtractUrls(_lastMessageText) : new List<string>();
                bool duplicateUrl = urls.Any(u => previousUrls.Contains(u)) && (DateTime.Now - _lastMessageTime) < _duplicateInterval;
                if (duplicateUrl)
                {
                    AddSystemMessage("同じURLを短時間で連続して送信できません。");
                    return;
                }

                foreach (var url in urls)
                {
                    var isSafe = await _safeBrowsing.IsUrlSafeAsync(url);
                    if (!isSafe)
                    {
                        AddSystemMessage($"安全でないURLが含まれているため、メッセージをブロックしました: {url}");
                        return;
                    }
                }
            }

            var msg = new ChatMessage
            {
                Type = ChatMessageType.Normal,
                UserName = UserName,
                Text = text,
                Timestamp = DateTime.Now,
                RoomId = CurrentRoomId
            };

            AddLocalMessage(msg);
#pragma warning disable CS4014
            _supabase.SendMessageAsync(msg);
#pragma warning restore CS4014

            _lastMessageText = text;
            _lastMessageTime = DateTime.Now;

            MessageText = string.Empty;
        }

        private static List<string> ExtractUrls(string text)
        {
            var urls = new List<string>();
            var regex = new Regex(@"https?:\/\/(www\.)?[-a-zA-Z0-9@:%._\+~#=]{1,256}\.[a-zA-Z0-9()]{1,6}\b([-a-zA-Z0-9()@:%_\+.~#?&//=]*)", RegexOptions.IgnoreCase);
            foreach (Match match in regex.Matches(text))
            {
                urls.Add(match.Value);
            }
            return urls;
        }

        private void CopyRoomId()
        {
            if (!IsAgreed) return;
            if (!string.IsNullOrWhiteSpace(CurrentRoomId))
            {
                Clipboard.SetText(CurrentRoomId);
                AddSystemMessage("ルームIDをクリップボードにコピーしました");
            }
        }

        private void AddRemoteMessage(ChatMessage msg)
        {
            if (msg.UserName == UserName) return;
            if (string.IsNullOrEmpty(msg.RoomId) || msg.RoomId != CurrentRoomId) return;
            Application.Current.Dispatcher.Invoke((Action)(() => Messages.Add(msg)));
        }

        private void AddLocalMessage(ChatMessage msg)
        {
            Application.Current.Dispatcher.Invoke((Action)(() => Messages.Add(msg)));
        }

        private void AddSystemMessage(string text)
        {
            var msg = new ChatMessage
            {
                Type = ChatMessageType.System,
                Text = text,
                Timestamp = DateTime.Now,
                RoomId = CurrentRoomId
            };
            Application.Current.Dispatcher.Invoke((Action)(() => Messages.Add(msg)));
        }

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            _selectionDelayTimer?.Stop();
            _selectionDelayTimer = null;
            _switchLock?.Dispose();
            _supabase?.Dispose();
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}