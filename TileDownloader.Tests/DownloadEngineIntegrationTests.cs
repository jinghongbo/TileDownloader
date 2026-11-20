using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NetTopologySuite.Geometries;
using TileDownloader.Models;
using TileDownloader.Services;
using Xunit;

namespace TileDownloader.Tests
{
    /// <summary>
    /// 下载引擎端到端：初始化/并发下载/落盘/收尾全部走新的线程池异步结构后，
    /// 仍能正确下载瓦片并支持断点续传（防止为了不卡界面把逻辑改坏）。
    /// </summary>
    public class DownloadEngineIntegrationTests
    {
        /// <summary>1×1 透明 PNG</summary>
        private static readonly byte[] TinyPng = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

        private sealed class CountingProgress : IDownloadProgress
        {
            public int Done;
            public int Errors;

            public void ReportLevelTotal(int z, long total) { }

            public void ReportTileDone(int z, int x, int y, bool skipped) => Interlocked.Increment(ref Done);

            public void ReportBytes(long bytes) { }

            public void ReportError(int z, int x, int y, string message) => Interlocked.Increment(ref Errors);

            public void SetState(string state) { }
        }

        [Fact]
        public async Task RunAsync_DownloadsTilesAndResumesWithRealStore()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var requestCount = 0;
            var acceptLoop = AcceptAsync(listener, () => Interlocked.Increment(ref requestCount));

            var dir = Path.Combine(Path.GetTempPath(), "td-engine-e2e-" + Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(dir);
            var dbPath = Path.Combine(dir, "tiles.mbtiles");
            var options = new TileTaskOptions
            {
                OutputPath = dbPath,
                SourceName = "local",
                MinX = -1,
                MaxX = 1,
                MinY = -1,
                MaxY = 1,
                MinLevel = 0,
                MaxLevel = 0,
            };

            try
            {
                var request = new TileDownloadRequest
                {
                    Source = new DownloadSource
                    {
                        Name = "local",
                        Url = $"http://127.0.0.1:{port}/{{z}}/{{x}}/{{y}}.png",
                    },
                    Range = new Envelope(options.MinX, options.MaxX, options.MinY, options.MaxY),
                    MinLevel = 0,
                    MaxLevel = 0,
                    OutputPath = dbPath,
                    FormatId = "MBTiles",
                };

                var progress = new CountingProgress();
                var engine = new TileDownloadEngine(new TileStoreRegistry(), new GoogleHostsService(), new GlobalDownloadLimiter(4));
                await engine.RunAsync(request, progress, CancellationToken.None);

                Assert.True(progress.Done > 0);
                Assert.Equal(0, progress.Errors);
                Assert.True(requestCount > 0);

                // 续传：重新打开同一个 .mbtiles，已下载瓦片必须被识别为已存在
                var store = new MbTilesTileStore();
                await store.InitializeAsync(options, CancellationToken.None);
                Assert.True(await store.TileExistsAsync(0, 0, 0, CancellationToken.None));
                await store.FinalizeAsync(CancellationToken.None);
            }
            finally
            {
                listener.Stop();
                try { await acceptLoop; } catch { }
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        private static Task AcceptAsync(TcpListener listener, Action onRequest)
        {
            return Task.Run(async () =>
            {
                while (true)
                {
                    TcpClient client;
                    try
                    {
                        client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    }
                    catch
                    {
                        return;
                    }

                    _ = Task.Run(async () =>
                    {
                        using (client)
                        {
                            try
                            {
                                var stream = client.GetStream();
                                var buffer = new byte[4096];
                                var text = new StringBuilder();
                                while (!text.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                                {
                                    var read = await stream.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
                                    if (read <= 0)
                                    {
                                        return;
                                    }
                                    text.Append(Encoding.ASCII.GetString(buffer, 0, read));
                                }

                                onRequest();
                                var header = "HTTP/1.1 200 OK\r\nContent-Type: image/png\r\n" +
                                             $"Content-Length: {TinyPng.Length}\r\nConnection: close\r\n\r\n";
                                await stream.WriteAsync(Encoding.ASCII.GetBytes(header)).ConfigureAwait(false);
                                await stream.WriteAsync(TinyPng).ConfigureAwait(false);
                            }
                            catch
                            {
                                // 客户端提前断开：忽略
                            }
                        }
                    });
                }
            });
        }
    }
}
