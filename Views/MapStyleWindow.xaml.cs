using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TileDownloader.Models;
using TileDownloader.Models.Styles;
using TileDownloader.Services.Styles;

namespace TileDownloader.Views
{
    /// <summary>
    /// 地图样式窗口：只做文本编辑 + 校验 + 示例 + 样式库，不做颜色调试。
    /// </summary>
    public partial class MapStyleWindow : Window
    {
        private readonly DownloadSource _source;
        private readonly IMapStyleAdapter _adapter;
        private readonly MapStyleStore _store;
        private bool _loading;

        public string? ResultText { get; private set; }

        public string? ResultName { get; private set; }

        public MapStyleWindow(
            DownloadSource source,
            IMapStyleAdapter adapter,
            MapStyleStore store,
            string? initialText,
            string? initialName)
        {
            InitializeComponent();

            _source = source;
            _adapter = adapter;
            _store = store;

            SourceText.Text = source.Name;
            PresetBox.ItemsSource = adapter.Presets;

            _loading = true;
            Editor.Text = initialText ?? string.Empty;
            StyleNameBox.Text = string.IsNullOrWhiteSpace(initialName) || initialName == "原生" ? "我的样式" : initialName;
            _loading = false;

            RefreshSaved();
            ValidateCurrent();
        }

        private void RefreshSaved()
        {
            var selectedId = (SavedList.SelectedItem as MapStyleDocument)?.Id;
            var items = _store.Styles
                .Where(s => string.Equals(s.ProviderId, _adapter.Id, StringComparison.OrdinalIgnoreCase))
                .OrderBy(s => s.Name, StringComparer.CurrentCulture)
                .ToList();

            _loading = true;
            SavedList.ItemsSource = items;
            if (!string.IsNullOrWhiteSpace(selectedId))
            {
                SavedList.SelectedItem = items.FirstOrDefault(s => s.Id == selectedId);
            }
            _loading = false;
        }

        private void PresetBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || PresetBox.SelectedItem is not MapStyleDocument preset)
            {
                return;
            }

            Editor.Text = preset.Text;
            StyleNameBox.Text = preset.Name;
            SetStatus($"已载入示例「{preset.Name}」，点「应用」生效", null);
        }

        private void SavedList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || SavedList.SelectedItem is not MapStyleDocument document)
            {
                return;
            }

            Editor.Text = document.Text;
            StyleNameBox.Text = document.Name;
            ValidateCurrent();
        }

        private void Validate_Click(object sender, RoutedEventArgs e) => ValidateCurrent();

        private void ValidateCurrent()
        {
            var result = _adapter.Parse(Editor.Text);
            if (!result.Ok)
            {
                SetStatus("✗ " + result.Error, Brushes.IndianRed);
                return;
            }

            var text = $"✓ 文本可用 · {result.RuleCount} 条规则";
            if (result.Warnings.Count > 0)
            {
                text += $" · {result.Warnings.Count} 条提示（不影响应用）";
                var first = result.Warnings[0];
                text += Environment.NewLine + "· " + first;
            }

            SetStatus(text, result.Warnings.Count > 0 ? Brushes.DarkOrange : Brushes.SeaGreen);
        }

        private void SetStatus(string text, Brush? brush)
        {
            StatusText.Text = text;
            StatusText.Foreground = brush ?? (Brush)FindResource("TextFillColorSecondaryBrush");
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            var name = StyleNameBox.Text?.Trim() ?? string.Empty;
            if (name.Length == 0)
            {
                SetStatus("✗ 请先填写样式名", Brushes.IndianRed);
                return;
            }

            var existing = SavedList.SelectedItem as MapStyleDocument;
            var document = new MapStyleDocument
            {
                Id = existing?.Id ?? Guid.NewGuid().ToString("n"),
                Name = name,
                ProviderId = _adapter.Id,
                Text = Editor.Text ?? string.Empty,
            };

            _store.Upsert(document);
            RefreshSaved();
            SavedList.SelectedItem = (_store.Styles.FirstOrDefault(s => s.Id == document.Id));
            SetStatus($"✓ 已保存到「我的样式」：{name}", Brushes.SeaGreen);
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (SavedList.SelectedItem is not MapStyleDocument document)
            {
                SetStatus("请先在左侧选择要删除的样式", Brushes.DarkOrange);
                return;
            }

            _store.Delete(document.Id);
            RefreshSaved();
            SetStatus($"已删除「{document.Name}」", null);
        }

        private void Paste_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var text = Clipboard.GetText();
                if (string.IsNullOrWhiteSpace(text))
                {
                    SetStatus("剪贴板是空的", Brushes.DarkOrange);
                    return;
                }

                Editor.Text = text;
                ValidateCurrent();
            }
            catch (Exception ex)
            {
                SetStatus("读取剪贴板失败：" + ex.Message, Brushes.IndianRed);
            }
        }

        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(Editor.Text ?? string.Empty);
                SetStatus("✓ 已复制当前样式文本", Brushes.SeaGreen);
            }
            catch (Exception ex)
            {
                SetStatus("写入剪贴板失败：" + ex.Message, Brushes.IndianRed);
            }
        }

        private void CopyPrompt_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var scenario = ScenarioBox.SelectedIndex switch
                {
                    1 => "light",
                    2 => "contrast",
                    _ => "dark",
                };

                Clipboard.SetText(_adapter.BuildAiPrompt(scenario));
                SetStatus("✓ AI 提示词已复制，粘贴到对话里补充需求即可", Brushes.SeaGreen);
            }
            catch (Exception ex)
            {
                SetStatus("写入剪贴板失败：" + ex.Message, Brushes.IndianRed);
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            var result = _adapter.Parse(Editor.Text);
            if (!result.Ok)
            {
                SetStatus("✗ 无法应用：" + result.Error, Brushes.IndianRed);
                return;
            }

            ResultText = Editor.Text ?? string.Empty;
            ResultName = ResolveName();
            DialogResult = true;
        }

        private string ResolveName()
        {
            var text = Editor.Text ?? string.Empty;

            if (SavedList.SelectedItem is MapStyleDocument saved && string.Equals(saved.Text, text, StringComparison.Ordinal))
            {
                return saved.Name;
            }

            var preset = _adapter.Presets.FirstOrDefault(p => string.Equals(p.Text, text, StringComparison.Ordinal));
            if (preset != null)
            {
                return preset.Name;
            }

            var name = StyleNameBox.Text?.Trim();
            return string.IsNullOrWhiteSpace(name) ? "自定义" : name;
        }
    }
}
