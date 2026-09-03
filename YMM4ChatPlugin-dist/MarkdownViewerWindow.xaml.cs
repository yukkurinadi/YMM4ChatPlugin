using System;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Threading;

namespace YMM4ChatPlugin
{
    /// <summary>
    /// マークダウンテキストを FlowDocument としてレンダリングするビューアウィンドウ。
    /// 背景は YMM4 のテーマ（SystemColors）に、文字色は ThemeColors
    /// （ChatPlugin_Fg）に追従する。テーマ変更時はリアクティブに文字色を更新する。
    /// </summary>
    public partial class MarkdownViewerWindow : Window
    {
        public MarkdownViewerWindow(string title, string markdown)
        {
            InitializeComponent();
            Title = title;
            DocumentViewer.Document = BuildDocument(markdown);
            ApplyThemeColors();

            // YMM4 のテーマ変更（リソース辞書の差し替え）を追跡して文字色を更新
            if (Application.Current?.Resources?.MergedDictionaries is INotifyCollectionChanged nc)
            {
                nc.CollectionChanged += (_, _) =>
                    Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)ApplyThemeColors);
            }
        }

        // ── テーマ文字色適用 ──────────────────────────────────────────────

        /// <summary>
        /// 現在の YMM4 テーマに合わせて、ウィンドウ・閉じるボタン・
        /// マークダウン本文の文字色を更新する。
        /// </summary>
        private void ApplyThemeColors()
        {
            var fg = ThemeColors.GetForegroundBrush();

            Foreground = fg;                // ウィンドウ内の既定文字色（本文以外の UI）
            CloseButton.Foreground = fg;    // 閉じるボタンの文字色
            if (DocumentViewer.Document is { } doc)
                doc.Foreground = fg;        // マークダウン本文（段落・見出しへ継承）
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        // ── Markdown → FlowDocument ──────────────────────────────────────

        private FlowDocument BuildDocument(string markdown)
        {
            var doc = new FlowDocument
            {
                FontFamily  = new FontFamily("Segoe UI, Meiryo UI, Yu Gothic UI"),
                FontSize    = 13,
                PagePadding = new Thickness(24, 16, 24, 24),
                TextAlignment = TextAlignment.Left,
                // Foreground は BuildDocument 後に ApplyThemeColors() が
                // テーマ（明暗）に合わせて上書きする
                Foreground  = SystemColors.WindowTextBrush,
                Background  = Brushes.Transparent,
                LineHeight  = 22,
            };

            var lines = markdown.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');

            List?     currentList     = null;
            bool      inOrderedList   = false;
            ListItem? lastOrderedItem = null; // ネスト箇条書き用

            // ローカル関数：currentList が null またはタイプが違えば新規作成して doc に追加
            void EnsureList(bool ordered)
            {
                if (currentList == null || inOrderedList != ordered)
                {
                    currentList = new List
                    {
                        MarkerStyle = ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                        Margin      = new Thickness(24, 2, 0, 2),
                    };
                    inOrderedList = ordered;
                    doc.Blocks.Add(currentList);
                }
            }

            void ResetList()
            {
                currentList     = null;
                lastOrderedItem = null;
            }

            foreach (var rawLine in lines)
            {
                string line = rawLine.TrimEnd();

                // ── H1: # 見出し ─────────────────────────────────────
                if (line.StartsWith("# "))
                {
                    ResetList();
                    doc.Blocks.Add(new Paragraph(new Run(line[2..]))
                    {
                        FontSize   = 20,
                        FontWeight = FontWeights.Bold,
                        Margin     = new Thickness(0, 4, 0, 8),
                        BorderBrush     = SystemColors.ControlDarkBrush,
                        BorderThickness = new Thickness(0, 0, 0, 1),
                        Padding    = new Thickness(0, 0, 0, 4),
                    });
                    continue;
                }

                // ── H2: ## 見出し ────────────────────────────────────
                if (line.StartsWith("## "))
                {
                    ResetList();
                    doc.Blocks.Add(new Paragraph(new Run(line[3..]))
                    {
                        FontSize   = 15,
                        FontWeight = FontWeights.Bold,
                        Margin     = new Thickness(0, 12, 0, 2),
                    });
                    continue;
                }

                // ── ネスト箇条書き（2文字以上のインデント + "- "）────
                var nestedMatch = Regex.Match(line, @"^ {2,}- (.+)$");
                if (nestedMatch.Success)
                {
                    if (lastOrderedItem != null)
                    {
                        // 直前の番号付きリストアイテムにネストリストを追加
                        List? nested = null;
                        foreach (var block in lastOrderedItem.Blocks)
                        {
                            if (block is List l) { nested = l; break; }
                        }
                        if (nested == null)
                        {
                            nested = new List
                            {
                                MarkerStyle = TextMarkerStyle.Circle,
                                Margin      = new Thickness(16, 0, 0, 0),
                            };
                            lastOrderedItem.Blocks.Add(nested);
                        }
                        nested.ListItems.Add(new ListItem(
                            new Paragraph(ParseInline(nestedMatch.Groups[1].Value))));
                    }
                    else
                    {
                        // 親アイテムがない場合は通常の箇条書きとして処理
                        EnsureList(false);
                        currentList!.ListItems.Add(new ListItem(
                            new Paragraph(ParseInline(nestedMatch.Groups[1].Value))));
                    }
                    continue;
                }

                // ── 番号付きリスト: N. text ──────────────────────────
                var orderedMatch = Regex.Match(line, @"^\d+\. (.+)$");
                if (orderedMatch.Success)
                {
                    EnsureList(true);
                    var item = new ListItem(new Paragraph(ParseInline(orderedMatch.Groups[1].Value)));
                    currentList!.ListItems.Add(item);
                    lastOrderedItem = item;
                    continue;
                }

                // ── 箇条書き: - text ─────────────────────────────────
                if (line.StartsWith("- "))
                {
                    EnsureList(false);
                    var item = new ListItem(new Paragraph(ParseInline(line[2..])));
                    currentList!.ListItems.Add(item);
                    lastOrderedItem = null;
                    continue;
                }

                // ── 空行 ─────────────────────────────────────────────
                if (string.IsNullOrWhiteSpace(line))
                {
                    ResetList();
                    continue;
                }

                // ── 通常段落 ─────────────────────────────────────────
                ResetList();
                doc.Blocks.Add(new Paragraph(ParseInline(line))
                {
                    Margin = new Thickness(0, 2, 0, 2),
                });
            }

            return doc;
        }

        // ── インライン要素パーサー ──────────────────────────────────────

        /// <summary>
        /// **bold** と [text](url) を Span/Bold/Hyperlink に変換する。
        /// </summary>
        private static Inline ParseInline(string text)
        {
            var span    = new Span();
            var pattern = new Regex(@"\*\*([^*]+)\*\*|\[([^\]]+)\]\(([^)]+)\)");
            int lastIdx = 0;

            foreach (Match m in pattern.Matches(text))
            {
                // トークン前のプレーンテキスト
                if (m.Index > lastIdx)
                    span.Inlines.Add(new Run(text[lastIdx..m.Index]));

                if (m.Groups[1].Success)
                {
                    // **bold**
                    span.Inlines.Add(new Bold(new Run(m.Groups[1].Value)));
                }
                else
                {
                    // [text](url)
                    var linkText = m.Groups[2].Value;
                    var linkUrl  = m.Groups[3].Value;

                    if (Uri.TryCreate(linkUrl, UriKind.Absolute, out var uri))
                    {
                        var link = new Hyperlink(new Run(linkText)) { NavigateUri = uri };
                        link.RequestNavigate += OnRequestNavigate;
                        span.Inlines.Add(link);
                    }
                    else
                    {
                        span.Inlines.Add(new Run(linkText));
                    }
                }

                lastIdx = m.Index + m.Length;
            }

            // 末尾の残りテキスト
            if (lastIdx < text.Length)
                span.Inlines.Add(new Run(text[lastIdx..]));

            // 特殊トークンがなければシンプルな Run を返す
            if (span.Inlines.Count == 0)
                return new Run(text);

            return span;
        }

        private static void OnRequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo(e.Uri.ToString()) { UseShellExecute = true }); }
            catch { /* 無視 */ }
            e.Handled = true;
        }

        // ── 埋め込みリソース読み込み ────────────────────────────────────

        /// <summary>
        /// アセンブリに埋め込まれた .md ファイルをテキストとして読み込む。
        /// </summary>
        public static string LoadEmbeddedMarkdown(string resourceName)
        {
            var asm = Assembly.GetExecutingAssembly();
            using var stream = asm.GetManifestResourceStream(resourceName);
            if (stream == null)
                return $"（リソースが見つかりません: {resourceName}）";
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
            return reader.ReadToEnd();
        }
    }
}
