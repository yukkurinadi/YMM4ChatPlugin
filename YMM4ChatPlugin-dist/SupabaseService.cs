using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;

namespace YMM4ChatPlugin
{
    public class RoomInfo
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public bool IsPinned { get; set; } = false;
    }

    public class SupabaseService : IDisposable
    {
        private readonly HttpClient _httpClient;
        private ClientWebSocket? _webSocket;
        private CancellationTokenSource? _wsCts;
        private string? _currentRoomId;
        private bool _isConnected;
        private string? _currentUserName;
        private CancellationTokenSource? _joinCts;

        public event Action<ChatMessage>? OnMessageReceived;
        public event Action<string>? OnConnected;
        public event Action<string>? OnError;
        public event Action<string>? OnRoomChanged;

        // ★ 配布用: API情報は削除済み。ビルド時に自分のSupabaseプロジェクト情報に置き換えてください ★
        private const string SupabaseUrl = "https://YOUR_PROJECT.supabase.co";
        private const string SupabaseKey = "YOUR_SUPABASE_ANON_KEY";

        public bool IsConnected => _isConnected;
        public string? CurrentRoomId => _currentRoomId;

        public SupabaseService()
        {
            _httpClient = new HttpClient();
            _httpClient.BaseAddress = new Uri(SupabaseUrl);
            _httpClient.Timeout = TimeSpan.FromSeconds(90);

            string cleanedKey = CleanAsciiString(SupabaseKey);
            _httpClient.DefaultRequestHeaders.Add("apikey", cleanedKey);
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {cleanedKey}");
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        private static string CleanAsciiString(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;
            return new string(input.Where(c => c >= 32 && c <= 126).ToArray());
        }

        public void SetCurrentUserName(string userName) => _currentUserName = userName;

        // 初期化（接続確認）
        public async Task<bool> InitializeAsync()
        {
            try
            {
                var response = await _httpClient.GetAsync("/rest/v1/rooms?limit=1");
                if (response.IsSuccessStatusCode)
                {
                    _isConnected = true;
                    OnConnected?.Invoke("Supabase接続成功");
                    return true;
                }
                var error = await response.Content.ReadAsStringAsync();
                OnError?.Invoke($"接続確認失敗: {error}");
                return false;
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"初期化エラー: {ex.Message}");
                return false;
            }
        }

        // ルーム一覧取得（ピン留め順、作成日順）
        public async Task<List<RoomInfo>> LoadRoomsAsync()
        {
            try
            {
                var response = await _httpClient.GetAsync("/rest/v1/rooms?order=is_pinned.desc,created_at.asc");
                if (!response.IsSuccessStatusCode) return new List<RoomInfo>();
                var json = await response.Content.ReadAsStringAsync();
                var rooms = JsonSerializer.Deserialize<List<JsonElement>>(json) ?? new List<JsonElement>();
                var list = new List<RoomInfo>();
                foreach (var r in rooms)
                {
                    list.Add(new RoomInfo
                    {
                        Id = r.GetProperty("id").GetString() ?? "",
                        Name = r.GetProperty("name").GetString() ?? "無名ルーム",
                        CreatedAt = r.GetProperty("created_at").GetDateTime(),
                        IsPinned = r.TryGetProperty("is_pinned", out var pinned) && pinned.GetBoolean()
                    });
                }
                return list;
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"ルーム一覧取得失敗: {ex.Message}");
                return new List<RoomInfo>();
            }
        }

        // 新しいルームを作成（ルーム名指定）
        public async Task<string> CreateRoomAsync(string roomName)
        {
            try
            {
                string roomId = GenerateShortId();
                var room = new { id = roomId, name = roomName, last_active = DateTime.UtcNow };
                var content = new StringContent(JsonSerializer.Serialize(room), Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync("/rest/v1/rooms", content);
                if (response.IsSuccessStatusCode) return roomId;
                var error = await response.Content.ReadAsStringAsync();
                OnError?.Invoke($"ルーム作成失敗: {error}");
                return "";
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"ルーム作成失敗: {ex.Message}");
                return "";
            }
        }

        // ルームに参加（切り替え）＋ WebSocket再接続
        public async Task<bool> JoinRoomAsync(string roomId)
        {
            if (string.IsNullOrEmpty(roomId)) return false;

            _joinCts?.Cancel();
            _joinCts?.Dispose();
            _joinCts = new CancellationTokenSource();

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(_joinCts.Token);
                cts.CancelAfter(TimeSpan.FromSeconds(45));
                var response = await _httpClient.GetAsync($"/rest/v1/rooms?id=eq.{roomId}&select=id", cts.Token);
                if (!response.IsSuccessStatusCode) return false;
                var content = await response.Content.ReadAsStringAsync(cts.Token);
                var rooms = JsonSerializer.Deserialize<List<dynamic>>(content);
                if (rooms == null || rooms.Count == 0) return false;

                if (_currentRoomId == roomId) return true;

                await DisconnectRealtimeAsync();
                _currentRoomId = roomId;
                await ConnectRealtimeWithTimeoutAsync(roomId, cts.Token);
                OnRoomChanged?.Invoke(roomId);
                return true;
            }
            catch (OperationCanceledException)
            {
                OnError?.Invoke($"ルーム参加タイムアウトまたはキャンセル: {roomId}");
                return false;
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"ルーム参加失敗: {ex.Message}");
                return false;
            }
        }

        private async Task ConnectRealtimeWithTimeoutAsync(string roomId, CancellationToken ct)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(25));
            await ConnectRealtimeAsync(roomId, timeoutCts.Token);
        }

        private async Task ConnectRealtimeAsync(string roomId, CancellationToken ct)
        {
            _wsCts?.Cancel();
            _webSocket?.Dispose();
            _webSocket = new ClientWebSocket();
            _wsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            var wsUrl = $"{SupabaseUrl.Replace("https", "wss")}/realtime/v1/websocket?apikey={CleanAsciiString(SupabaseKey)}&vsn=1.0.0";
            await _webSocket.ConnectAsync(new Uri(wsUrl), _wsCts.Token);

            var joinMsg = new
            {
                topic = $"realtime:public:messages:room_id=eq.{roomId}",
                @event = "phx_join",
                payload = new { headers = new { } },
                @ref = "1"
            };
            var joinJson = JsonSerializer.Serialize(joinMsg);
            await _webSocket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(joinJson)), WebSocketMessageType.Text, true, _wsCts.Token);

            _ = Task.Run(() => ReceiveMessagesLoop(_webSocket, _wsCts.Token), _wsCts.Token);
        }

        private async Task ReceiveMessagesLoop(ClientWebSocket socket, CancellationToken ct)
        {
            var buffer = new byte[4096];
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                try
                {
                    var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        var msgJson = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        if (msgJson.Contains("\"event\":\"INSERT\"") && msgJson.Contains("\"table\":\"messages\""))
                        {
                            using var doc = JsonDocument.Parse(msgJson);
                            var record = doc.RootElement.GetProperty("payload").GetProperty("record");
                            var chatMsg = new ChatMessage
                            {
                                Id = record.GetProperty("id").ToString(),
                                UserName = record.GetProperty("user_name").ToString(),
                                Text = record.GetProperty("text").ToString(),
                                Type = record.GetProperty("type").ToString() == "JoinLeave" ? ChatMessageType.JoinLeave :
                                       record.GetProperty("type").ToString() == "System" ? ChatMessageType.System : ChatMessageType.Normal,
                                Timestamp = record.GetProperty("created_at").GetDateTime(),
                                ReplyToId = record.TryGetProperty("reply_to_id", out var rid) ? rid.ToString() : null,
                                ReplyPreview = record.TryGetProperty("reply_preview", out var rp) ? rp.ToString() : null,
                                RoomId = record.GetProperty("room_id").ToString()
                            };
                            OnMessageReceived?.Invoke(chatMsg);
                        }
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { OnError?.Invoke($"WebSocket受信エラー: {ex.Message}"); break; }
            }
        }

        private async Task DisconnectRealtimeAsync()
        {
            if (_webSocket != null && _webSocket.State == WebSocketState.Open)
            {
                try
                {
                    await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                }
                catch { }
            }
            _wsCts?.Cancel();
            _webSocket?.Dispose();
        }

        // メッセージ送信（REST API）
        public async Task SendMessageAsync(ChatMessage message)
        {
            if (string.IsNullOrEmpty(_currentRoomId)) return;
            try
            {
                var record = new
                {
                    room_id = _currentRoomId,
                    user_name = message.UserName,
                    text = message.Text,
                    type = message.Type.ToString(),
                    created_at = DateTime.UtcNow,
                    reply_to_id = message.ReplyToId,
                    reply_preview = message.ReplyPreview
                };
                var content = new StringContent(JsonSerializer.Serialize(record), Encoding.UTF8, "application/json");
                await _httpClient.PostAsync("/rest/v1/messages", content);
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"メッセージ送信失敗: {ex.Message}");
            }
        }

        // 履歴読み込み（最新100件）
        public async Task<List<ChatMessage>> LoadHistoryAsync(int limit = 100)
        {
            if (string.IsNullOrEmpty(_currentRoomId)) return new List<ChatMessage>();
            try
            {
                var url = $"/rest/v1/messages?room_id=eq.{_currentRoomId}&order=created_at.asc&limit={limit}";
                var response = await _httpClient.GetAsync(url);
                if (!response.IsSuccessStatusCode) return new List<ChatMessage>();
                var json = await response.Content.ReadAsStringAsync();
                var records = JsonSerializer.Deserialize<List<JsonElement>>(json);
                var messages = new List<ChatMessage>();
                if (records != null)
                {
                    foreach (var item in records)
                    {
                        messages.Add(new ChatMessage
                        {
                            Id = item.GetProperty("id").ToString(),
                            UserName = item.GetProperty("user_name").ToString(),
                            Text = item.GetProperty("text").ToString(),
                            Type = item.GetProperty("type").ToString() == "JoinLeave" ? ChatMessageType.JoinLeave :
                                   item.GetProperty("type").ToString() == "System" ? ChatMessageType.System : ChatMessageType.Normal,
                            Timestamp = item.GetProperty("created_at").GetDateTime(),
                            ReplyToId = item.TryGetProperty("reply_to_id", out var rid) ? rid.ToString() : null,
                            ReplyPreview = item.TryGetProperty("reply_preview", out var rp) ? rp.ToString() : null,
                            RoomId = item.GetProperty("room_id").ToString()
                        });
                    }
                }
                return messages;
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"履歴読み込み失敗: {ex.Message}");
                return new List<ChatMessage>();
            }
        }

        // 短いルームIDを生成（6桁英数字）
        private static string GenerateShortId()
        {
            var chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
            var random = new Random();
            var result = new char[6];
            for (int i = 0; i < 6; i++) result[i] = chars[random.Next(chars.Length)];
            return new string(result);
        }

        public void Dispose()
        {
            _joinCts?.Cancel();
            _joinCts?.Dispose();
            _wsCts?.Cancel();
            _webSocket?.Dispose();
            _httpClient?.Dispose();
        }
    }
}