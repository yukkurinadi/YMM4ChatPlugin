using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;

namespace YMM4ChatPlugin
{
    internal static class AgreementSettings
    {
        private static readonly string DllDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
        private static readonly string AppDataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "YMM4ChatPlugin");

        // ★ 実際に使用する保存先フォルダ（判定後に決定）
        private static string _saveDirectory = "";
        private static string _filePath = "";

        // 保存先を決定するプロパティ（初回呼び出し時に判定）
        private static string SaveDirectory
        {
            get
            {
                if (string.IsNullOrEmpty(_saveDirectory))
                {
                    // Program Files 配下にインストールされているか判定
                    if (IsProgramFilesPath(DllDirectory))
                    {
                        _saveDirectory = AppDataDirectory;
                        System.Diagnostics.Debug.WriteLine($"設定保存先: AppData ({_saveDirectory})");
                    }
                    else
                    {
                        _saveDirectory = DllDirectory;
                        System.Diagnostics.Debug.WriteLine($"設定保存先: Pluginフォルダ ({_saveDirectory})");
                    }

                    // フォルダが存在しなければ作成
                    if (!Directory.Exists(_saveDirectory))
                        Directory.CreateDirectory(_saveDirectory);
                }
                return _saveDirectory;
            }
        }

        private static string FilePath
        {
            get
            {
                if (string.IsNullOrEmpty(_filePath))
                    _filePath = Path.Combine(SaveDirectory, "Agreement.json");
                return _filePath;
            }
        }

        // Program Files 配下かどうかを判定（32bit/64bit両方対応）
        private static bool IsProgramFilesPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var programFilesX64 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            // Program Files と Program Files (x86) の両方をチェック
            if (path.StartsWith(programFilesX86, StringComparison.OrdinalIgnoreCase))
                return true;
            if (path.StartsWith(programFilesX64, StringComparison.OrdinalIgnoreCase))
                return true;
            return false;
        }

        // 保存するデータ構造
        private class AgreementData
        {
            public bool IsAgreed { get; set; }
            public string UserName { get; set; } = "匿名";
        }

        // 設定を読み込む（新しい場所、旧場所の自動移行対応）
        public static (bool isAgreed, string userName) LoadAgreement()
        {
            string currentPath = FilePath;
            string oldPath = Path.Combine(DllDirectory, "Agreement.json");

            // 新しい場所にファイルがなければ、古い場所から移行（あればコピー）
            if (!File.Exists(currentPath) && File.Exists(oldPath))
            {
                try
                {
                    File.Copy(oldPath, currentPath);
                    System.Diagnostics.Debug.WriteLine($"古い設定を移行しました: {oldPath} → {currentPath}");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"設定移行失敗: {ex.Message}");
                }
            }

            try
            {
                if (!File.Exists(currentPath)) return (false, "匿名");
                string json = File.ReadAllText(currentPath);
                var data = JsonSerializer.Deserialize<AgreementData>(json);
                if (data != null)
                    return (data.IsAgreed, data.UserName);
                return (false, "匿名");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"LoadAgreement エラー: {ex.Message}");
                return (false, "匿名");
            }
        }

        public static void SaveAgreement(bool agreed, string userName)
        {
            try
            {
                var data = new AgreementData { IsAgreed = agreed, UserName = userName };
                string json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(FilePath, json);
                System.Diagnostics.Debug.WriteLine($"SaveAgreement 成功: {FilePath} = agreed={agreed}, userName={userName}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"SaveAgreement エラー: {ex.Message}");
                MessageBox.Show(
                    $"同意状態の保存に失敗しました。\n{ex.Message}\n\n保存先: {FilePath}\n\nフォルダの書き込み権限を確認してください。",
                    "エラー",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        // デバッグ用：現在の保存先パスを返す
        public static string GetFilePath() => FilePath;
    }
}