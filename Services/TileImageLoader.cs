using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using TileDownloader.Models;

namespace TileDownloader.Services
{
    /// <summary>
    /// 瓦片图加载服务实现：
    /// - 内存缓存（容量上限 ~256，超限按最近访问淘汰一半）
    /// - 磁盘缓存 %LOCALAPPDATA%\TileDownloader\tilecache\{源名}\{z}\{x}\{y}.png
    /// - HttpClient 按源缓存复用（UA/Referer/Cookies）
    /// 网络 IO 均在后台线程，调用方负责 UI 调度。
    /// </summary>
    public class TileImageLoader : ITileImageLoader
    {
        private class CacheEntry
        {
            public byte[] Data = Array.Empty<byte>();
            public long LastAccess;
        }

        private const int MemoryLimit = 256;

        private readonly ConcurrentDictionary<string, CacheEntry> _memory = new();
        private readonly object _evictLock = new();
        private readonly ConcurrentDictionary<string, HttpClient> _clients = new(StringComparer.Ordinal);
        private readonly string _diskRoot;
        private bool _useProxy = false;

        /// <summary>是否使用系统代理（默认直连；切换后清空缓存 client 重建）</summary>
        public bool UseProxy
        {
            get => _useProxy;
            set
            {
                if (_useProxy == value)
                {
                    return;
                }
                _useProxy = value;
                // 重建按源缓存的 client，使新设置立即生效
                foreach (var c in _clients.Values)
                {
                    try { c.Dispose(); } catch { }
                }
                _clients.Clear();
            }
        }

        public TileImageLoader()
        {
            var baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TileDownloader");
            _diskRoot = Path.Combine(baseDir, "tilecache");
        }

        public async Task<byte[]?> GetTileAsync(
            string sourceName, string urlTemplate, string[]? subdomains, string? apiKey,
            string userAgent, string referer, string cookies,
            int z, int x, int y, CancellationToken ct)
        {
            var diskFile = GetDiskPath(sourceName, z, x, y);

            // 1. 内存缓存
            var key = $"{sourceName}|{z}|{x}|{y}";
            if (_memory.TryGetValue(key, out var entry))
            {
                entry.LastAccess = Environment.TickCount64;
                return entry.Data;
            }

            // 2. 磁盘缓存
            try
            {
                if (File.Exists(diskFile))
                {
                    var diskBytes = await File.ReadAllBytesAsync(diskFile, ct);
                    PutMemory(key, diskBytes);
                    return diskBytes;
                }
            }
            catch
            {
                // 磁盘读取失败则走网络
            }

            // 3. 网络获取
            var url = TileUrlBuilder.BuildUrl(urlTemplate, subdomains, apiKey, z, x, y);
            var client = GetClient(sourceName, urlTemplate, userAgent, referer, cookies);
            try
            {
                using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseContentRead, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    // 404 等视为空瓦片，不缓存
                    return null;
                }

                var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                if (bytes.Length == 0)
                {
                    return null;
                }

                PutMemory(key, bytes);

                // 写磁盘缓存（失败不影响返回）
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(diskFile)!);
                    await File.WriteAllBytesAsync(diskFile, bytes, ct);
                }
                catch
                {
                }

                return bytes;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // 网络异常返回 null
                return null;
            }
        }

        /// <summary>获取（或创建）指定来源的 HttpClient</summary>
        private HttpClient GetClient(string sourceName, string urlTemplate, string userAgent, string referer, string cookies)
        {
            return _clients.GetOrAdd(sourceName, _ =>
            {
                // 用一个临时 DownloadSource 复用统一的 client 构造逻辑
                var source = new DownloadSource
                {
                    Name = sourceName,
                    Url = urlTemplate,
                    UserAgent = userAgent,
                    Referer = referer,
                    Cookies = cookies,
                };
                return TileUrlBuilder.CreateClient(source, _useProxy);
            });
        }

        /// <summary>写内存缓存，超限时淘汰一半最旧项</summary>
        private void PutMemory(string key, byte[] data)
        {
            if (_memory.Count >= MemoryLimit)
            {
                lock (_evictLock)
                {
                    if (_memory.Count >= MemoryLimit)
                    {
                        foreach (var k in _memory
                            .OrderBy(p => p.Value.LastAccess)
                            .Take(MemoryLimit / 2)
                            .Select(p => p.Key)
                            .ToList())
                        {
                            _memory.TryRemove(k, out _);
                        }
                    }
                }
            }

            _memory[key] = new CacheEntry { Data = data, LastAccess = Environment.TickCount64 };
        }

        private string GetDiskPath(string sourceName, int z, int x, int y)
        {
            // 源名做文件名安全化
            var safeName = string.Join("_", sourceName.Split(Path.GetInvalidFileNameChars()));
            if (string.IsNullOrWhiteSpace(safeName))
            {
                safeName = "default";
            }
            return Path.Combine(_diskRoot, safeName, z.ToString(), x.ToString(), y + ".png");
        }
    }
}
