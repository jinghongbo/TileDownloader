using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NetTopologySuite.Geometries;
using TileDownloader.Models;
using TileDownloader.Services;
using Xunit;

namespace TileDownloader.Tests
{
    /// <summary>
    /// 长任务不得占用调用线程（UI 线程）：存储初始化/续传预载必须在线程池执行，
    /// 否则点「开始下载」或启动下载任务后整个界面会卡住。
    /// </summary>
    public class TileDownloadEngineTests
    {
        private sealed class FakeTileStore : ITileStore
        {
            private readonly int _initializeDelayMs;

            public FakeTileStore(int initializeDelayMs) => _initializeDelayMs = initializeDelayMs;

            public int InitializeThreadId;
            public int FinalizeThreadId;
            public bool InitializeCompleted;
            public int ExistsCalls;

            public string FormatId => "Fake";
            public string DisplayName => "Fake";
            public string DefaultExtension => "";

            public Task InitializeAsync(TileTaskOptions options, CancellationToken ct)
            {
                InitializeThreadId = Environment.CurrentManagedThreadId;
                // 模拟大文件续传预载：同步阻塞必须发生在池线程，而不是调用线程
                Thread.Sleep(_initializeDelayMs);
                InitializeCompleted = true;
                return Task.CompletedTask;
            }

            public Task<bool> TileExistsAsync(int z, int x, int y, CancellationToken ct)
            {
                Interlocked.Increment(ref ExistsCalls);
                return Task.FromResult(true);
            }

            public Task SaveTileAsync(int z, int x, int y, byte[] data, CancellationToken ct) => Task.CompletedTask;

            public Task FinalizeAsync(CancellationToken ct)
            {
                FinalizeThreadId = Environment.CurrentManagedThreadId;
                return Task.CompletedTask;
            }
        }

        private sealed class FakeRegistry : TileStoreRegistry
        {
            private readonly ITileStore _store;

            public FakeRegistry(ITileStore store) => _store = store;

            public override ITileStore CreateStore(string formatId) => _store;
        }

        private sealed class CountingProgress : IDownloadProgress
        {
            public int Done;

            public void ReportLevelTotal(int z, long total) { }

            public void ReportTileDone(int z, int x, int y, bool skipped) => Interlocked.Increment(ref Done);

            public void ReportBytes(long bytes) { }

            public void ReportError(int z, int x, int y, string message) { }

            public void SetState(string state) { }
        }

        [Fact]
        public async Task RunAsync_DoesNotRunStoreInitializationOnCallerThread()
        {
            var store = new FakeTileStore(initializeDelayMs: 300);
            var engine = new TileDownloadEngine(new FakeRegistry(store), new GoogleHostsService(), new GlobalDownloadLimiter(2));
            var progress = new CountingProgress();
            var request = new TileDownloadRequest
            {
                Source = new DownloadSource { Name = "fake", Url = "https://example.com/{z}/{x}/{y}" },
                Range = new Envelope(0, 1, 0, 1),
                MinLevel = 0,
                MaxLevel = 0,
                OutputPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "td-engine-" + Guid.NewGuid().ToString("n")),
                FormatId = "Fake",
            };

            var sw = Stopwatch.StartNew();
            var run = engine.RunAsync(request, progress, CancellationToken.None);
            sw.Stop();

            // 若初始化在调用线程同步执行，这里会被 300ms 的预载阻塞
            Assert.True(sw.ElapsedMilliseconds < 150,
                $"RunAsync 在调用线程上同步执行了 {sw.ElapsedMilliseconds}ms，长任务会卡住界面");

            await run.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.True(store.InitializeCompleted);
            Assert.True(store.ExistsCalls > 0);
            Assert.NotEqual(0, store.FinalizeThreadId);
        }
    }
}