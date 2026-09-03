using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace YMM4ChatPlugin
{
    public class SafeBrowsingService
    {
        private readonly HttpClient _httpClient;
        // ★ ここにあなたの Safe Browsing API キーを設定してください ★
        private const string ApiKey = "";

        private const string SafeBrowsingUrl = "https://safebrowsing.googleapis.com/v4/threatMatches:find";

        public SafeBrowsingService()
        {
            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(5);
        }

        /// <summary>
        /// 指定されたURLが安全かどうかをチェックする
        /// </summary>
        /// <returns>true: 安全, false: 危険</returns>
        public async Task<bool> IsUrlSafeAsync(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return true;
            if (string.IsNullOrEmpty(ApiKey) || ApiKey == "YOUR_API_KEY_HERE")
            {
                // APIキーが設定されていない場合はチェックをスキップ（全て安全とみなす）
                System.Diagnostics.Debug.WriteLine("Safe Browsing APIキーが設定されていません。");
                return true;
            }

            try
            {
                var requestBody = new
                {
                    client = new
                    {
                        clientId = "YMM4ChatPlugin",
                        clientVersion = "1.0.0"
                    },
                    threatInfo = new
                    {
                        threatTypes = new[] { "MALWARE", "SOCIAL_ENGINEERING", "UNWANTED_SOFTWARE", "POTENTIALLY_HARMFUL_APPLICATION" },
                        platformTypes = new[] { "ANY_PLATFORM" },
                        threatEntryTypes = new[] { "URL" },
                        threatEntries = new[]
                        {
                            new { url = url }
                        }
                    }
                };

                var json = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync($"{SafeBrowsingUrl}?key={ApiKey}", content);
                var responseBody = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    // マッチがあれば危険、なければ安全
                    using var doc = JsonDocument.Parse(responseBody);
                    if (doc.RootElement.TryGetProperty("matches", out var matches))
                    {
                        return matches.GetArrayLength() == 0;
                    }
                    return true; // matchesプロパティがなければ安全
                }
                else
                {
                    // APIエラーの場合は念のため安全とみなす（運用判断）
                    System.Diagnostics.Debug.WriteLine($"Safe Browsing API error: {responseBody}");
                    return true;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Safe Browsing exception: {ex.Message}");
                return true; // エラー時は安全側に倒す
            }
        }
    }
}
