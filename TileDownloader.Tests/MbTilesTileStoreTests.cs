using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TileDownloader.Services;
using Xunit;

namespace TileDownloader.Tests
{
    /// <summary>
    /// MBTiles 续传存在性索引：分块位图必须与库内瓦片一致（漏报会重复下载，误报会漏下）
    /// </summary>
    public class MbTilesTileStoreTests
    {
        private static string CreateTempPath()
        {
            var dir = Path.Combine(Path.GetTempPath(), "td-mbtiles-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "tiles.mbtiles");
        }

        private static TileTaskOptions Options(string path, int minLevel = 3, int maxLevel = 5) => new()
        {
            OutputPath = path,
            SourceName = "test",
            MinX = 120.0,
            MaxX = 121.0,
            MinY = 30.0,
            MaxY = 31.0,
            MinLevel = minLevel,
            MaxLevel = maxLevel,
        };

        [Fact]
        public async Task SaveThenResume_DetectsOnlySavedTiles()
        {
            var path = CreateTempPath();
            try
            {
                var payload = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x00, 0x01 };
                var (x3, y3) = TileUrlBuilder.LatLonToTile(30.5, 120.5, 3);
                var (x4, y4) = TileUrlBuilder.LatLonToTile(30.5, 120.5, 4);

                var store = new MbTilesTileStore();
                await store.InitializeAsync(Options(path), CancellationToken.None);
                Assert.False(await store.TileExistsAsync(3, x3, y3, CancellationToken.None));
                await store.SaveTileAsync(3, x3, y3, payload, CancellationToken.None);
                await store.SaveTileAsync(4, x4, y4, payload, CancellationToken.None);
                await store.FinalizeAsync(CancellationToken.None);

                // 续传：新实例从文件预载位图后，已存瓦片为 true，未存瓦片为 false
                var resumed = new MbTilesTileStore();
                await resumed.InitializeAsync(Options(path), CancellationToken.None);
                Assert.True(await resumed.TileExistsAsync(3, x3, y3, CancellationToken.None));
                Assert.True(await resumed.TileExistsAsync(4, x4, y4, CancellationToken.None));
                Assert.False(await resumed.TileExistsAsync(3, x3 + 1, y3, CancellationToken.None));
                Assert.False(await resumed.TileExistsAsync(5, TileUrlBuilder.LatLonToTile(30.5, 120.5, 5).x,
                    TileUrlBuilder.LatLonToTile(30.5, 120.5, 5).y, CancellationToken.None));
                await resumed.FinalizeAsync(CancellationToken.None);
            }
            finally
            {
                TryDeleteDirectory(path);
            }
        }

        [Fact]
        public async Task TileOutsideConfirmedRectangle_FallsBackToDatabase()
        {
            var path = CreateTempPath();
            try
            {
                var payload = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x00, 0x01 };
                var (farX, farY) = TileUrlBuilder.LatLonToTile(-33.9, 151.2, 4);

                var store = new MbTilesTileStore();
                await store.InitializeAsync(Options(path), CancellationToken.None);
                // 直接写入一张范围外的瓦片（引擎不会这么做，这里模拟旧文件里已有的其它区域瓦片）
                await store.SaveTileAsync(4, farX, farY, payload, CancellationToken.None);
                await store.FinalizeAsync(CancellationToken.None);

                var reopened = new MbTilesTileStore();
                await reopened.InitializeAsync(Options(path), CancellationToken.None);
                Assert.True(await reopened.TileExistsAsync(4, farX, farY, CancellationToken.None));
                await reopened.FinalizeAsync(CancellationToken.None);
            }
            finally
            {
                TryDeleteDirectory(path);
            }
        }

        private static void TryDeleteDirectory(string file)
        {
            try
            {
                var dir = Path.GetDirectoryName(file);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.Delete(dir, true);
                }
            }
            catch
            {
                // 测试清理失败不改变结论
            }
        }
    }
}
