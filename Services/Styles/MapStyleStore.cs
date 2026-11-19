using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using TileDownloader.Models.Styles;

namespace TileDownloader.Services.Styles
{
    /// <summary>
    /// 样式库持久化：用户保存的样式 + 每个来源上次使用的样式。
    /// 文件：%LOCALAPPDATA%\TileDownloader\map_styles.json
    /// </summary>
    public class MapStyleStore
    {
        private sealed class StoreData
        {
            public List<MapStyleDocument> Styles { get; set; } = new();

            public Dictionary<string, MapStyleDocument> LastBySource { get; set; } = new(StringComparer.Ordinal);
        }

        private readonly string _filePath;
        private StoreData _data = new();

        public MapStyleStore()
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TileDownloader");
            _filePath = Path.Combine(dir, "map_styles.json");
            Load();
        }

        public IReadOnlyList<MapStyleDocument> Styles => _data.Styles;

        public void Upsert(MapStyleDocument document)
        {
            if (document == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(document.Id))
            {
                document.Id = Guid.NewGuid().ToString("n");
            }

            var index = _data.Styles.FindIndex(s => string.Equals(s.Id, document.Id, StringComparison.Ordinal));
            if (index >= 0)
            {
                _data.Styles[index] = document;
            }
            else
            {
                _data.Styles.Add(document);
            }

            Save();
        }

        public void Delete(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return;
            }

            if (_data.Styles.RemoveAll(s => string.Equals(s.Id, id, StringComparison.Ordinal)) > 0)
            {
                Save();
            }
        }

        public MapStyleDocument? GetLast(string sourceName)
        {
            if (string.IsNullOrWhiteSpace(sourceName))
            {
                return null;
            }

            return _data.LastBySource.TryGetValue(sourceName, out var document) ? document : null;
        }

        public void SetLast(string sourceName, MapStyleDocument? document)
        {
            if (string.IsNullOrWhiteSpace(sourceName))
            {
                return;
            }

            if (document == null || string.IsNullOrWhiteSpace(document.Text))
            {
                _data.LastBySource.Remove(sourceName);
            }
            else
            {
                _data.LastBySource[sourceName] = new MapStyleDocument
                {
                    Id = document.Id,
                    Name = document.Name,
                    ProviderId = document.ProviderId,
                    Text = document.Text,
                };
            }

            Save();
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(_filePath))
                {
                    return;
                }

                var json = File.ReadAllText(_filePath);
                _data = JsonConvert.DeserializeObject<StoreData>(json) ?? new StoreData();
                _data.Styles ??= new List<MapStyleDocument>();
                _data.LastBySource ??= new Dictionary<string, MapStyleDocument>(StringComparer.Ordinal);
            }
            catch
            {
                // 损坏时按空库处理，不阻塞启动
                _data = new StoreData();
            }
        }

        private void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
                File.WriteAllText(_filePath, JsonConvert.SerializeObject(_data, Formatting.Indented));
            }
            catch
            {
                // 持久化失败不影响本次会话
            }
        }
    }
}
