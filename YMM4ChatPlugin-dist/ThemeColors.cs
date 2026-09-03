using System.Windows;
using System.Windows.Media;

namespace YMM4ChatPlugin
{
    /// <summary>
    /// YMM4 テーマの明暗（Black/Dark か、ライト/カスタムか）を検出し、
    /// プラグイン共通の文字色リソースを Application.Resources に公開する共有クラス。
    /// ChatView と MarkdownViewerWindow が同じ文字色を使えるようにする。
    /// </summary>
    internal static class ThemeColors
    {
        // プラグイン専用の文字色リソースキー
        public const string ForegroundKey      = "ChatPlugin_Fg";
        public const string ForegroundMutedKey = "ChatPlugin_FgMuted";

        // ── テーマ文字色検出 ────────────────────────────────────────────────
        //
        // YMM4 の Black/Dark テーマは背景を暗くするが、
        // SystemColors.WindowTextBrushKey を更新しないことがある。
        // そのため、TryFindResource で現在の WindowBrush を取得して輝度を計算し、
        // プラグイン専用の ChatPlugin_Fg / ChatPlugin_FgMuted リソースに
        // 適切な文字色をセットする。
        //

        /// <summary>現在のテーマに合わせて文字色リソースを更新する。</summary>
        public static void UpdateTextColors()
        {
            var app = Application.Current;
            if (app == null) return;

            bool isDark = DetectDark(app);

            SetAppBrush(app, ForegroundKey,
                isDark ? Colors.White            : Colors.Black);
            SetAppBrush(app, ForegroundMutedKey,
                isDark ? Color.FromRgb(0xA0, 0xA0, 0xA0)
                       : Color.FromRgb(0x60, 0x60, 0x60));
        }

        /// <summary>現在のテーマに適した本文文字色ブラシを取得する。</summary>
        public static Brush GetForegroundBrush()
        {
            UpdateTextColors();
            return (Application.Current?.Resources[ForegroundKey] as Brush)
                   ?? SystemColors.WindowTextBrush;
        }

        private static bool DetectDark(Application app)
        {
            // 1) Application.Resources 経由で現在の WindowBrush を取得して輝度判定
            //    （YMM4 テーマが上書きしていれば正しい値が返る）
            if (app.TryFindResource(SystemColors.WindowBrushKey) is SolidColorBrush wb)
            {
                return Lum(wb.Color) < 0.5;
            }

            // 2) CustomThemePlugin / YMM4 が定義する Color700 の輝度で判定
            if (app.TryFindResource("Color700") is Color c700)
                return Lum(c700) < 0.5;

            // 3) Windows ダークモード設定
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("AppsUseLightTheme") is int i && i == 0;
            }
            catch { return false; }
        }

        /// <summary>知覚輝度 (0=黒, 1=白)</summary>
        private static double Lum(Color c)
            => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;

        private static void SetAppBrush(Application app, string key, Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            app.Resources[key] = brush;
        }
    }
}
