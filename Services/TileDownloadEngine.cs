using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using TileDownloader.Models;

namespace TileDownloader.Services
{
    /// <summary>
    /// 通用瓦片下载引擎（自研 XYZ 数学，不依赖 BruTile）：
    /// 对请求范围按层级枚举瓦片，HttpClient + SemaphoreSlim 并发 + 重试，
    /// 404 视为空瓦片跳过并计完成；每瓦片先查存储跳过已存在（断点续传）；
    /// 进度通过 IDownloadProgress 回调。
    /// </summary>
    public class TileDownloadEngine : IDownloadEngine
    {
        private readonly TileStoreRegistry _storeRegistry;
        private readonly GoogleHostsService _googleHostsService;

        public TileDownloadEngine(TileStoreRegistry storeRegistry, GoogleHostsService googleHostsService)
        {
            _storeRegistry = storeRegistry;
            _googleHostsService = googleHostsService;
        }

        public async Task RunAsync(TileDownloadRequest request, IDownloadProgress progress, CancellationToken ct)
        {
            var source = request.Source ?? throw new ArgumentException("下载来源不能为空");

            var store = _storeRegistry.CreateStore(request.FormatId);
            var options = new TileTaskOptions
            {
                OutputPath = request.OutputPath,
                SourceName = source.Name,
                MinX = request.Range.MinX,
                MinY = request.Range.MinY,
                MaxX = request.Range.MaxX,
                MaxY = request.Range.MaxY,
                MinLevel = request.MinLevel,
                MaxLevel = request.MaxLevel,
                FullBlock = request.FullBlock,
                // 落盘最终失败（重试仍失败）时把错误上报到进度/UI，避免静默丢瓦片
                OnTileWriteFailed = progress.ReportError,
            };
            await store.InitializeAsync(options, ct);

            try
            {
                // 每个源复用一个 HttpClient（UA/Referer/Cookies），直连模式下自动路由最快 Google IP
                using var client = TileUrlBuilder.CreateClient(source, request.UseProxy, _googleHostsService);
                var subdomains = source.GetSubdomains();

                var parallelOptions = new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Max(1, request.Concurrent),
                    CancellationToken = ct,
                };

                // 提前预先计算所有层级与总瓦片数，上报至进度汇报器建立预算
                // （完整块模式下按 pak 分块边界扩展，与下方实际枚举保持一致）
                for (var z = request.MinLevel; z <= request.MaxLevel; z++)
                {
                    var (firstCol, lastCol) = TileUrlBuilder.ColRange(request.Range.MinX, request.Range.MaxX, z, request.FullBlock);
                    var (firstRow, lastRow) = TileUrlBuilder.RowRange(request.Range.MinY, request.Range.MaxY, z, request.FullBlock);
                    var total = (long)(lastCol - firstCol + 1) * (lastRow - firstRow + 1);
                    progress.ReportLevelTotal(z, total);
                }

                for (var z = request.MinLevel; z <= request.MaxLevel; z++)
                {
                    ct.ThrowIfCancellationRequested();

                    var (firstCol, lastCol) = TileUrlBuilder.ColRange(request.Range.MinX, request.Range.MaxX, z, request.FullBlock);
                    var (firstRow, lastRow) = TileUrlBuilder.RowRange(request.Range.MinY, request.Range.MaxY, z, request.FullBlock);

                    static IEnumerable<(int x, int y)> EnumerateTiles(int fc, int lc, int fr, int lr)
                    {
                        for (var x = fc; x <= lc; x++)
                        {
                            for (var y = fr; y <= lr; y++)
                            {
                                yield return (x, y);
                            }
                        }
                    }

                    await Parallel.ForEachAsync(EnumerateTiles(firstCol, lastCol, firstRow, lastRow), parallelOptions, async (tile, token) =>
                    {
                        await DownloadTileAsync(client, store, progress, source, subdomains, z, tile.x, tile.y, request.Retry, token);
                    });
                }
            }
            finally
            {
                // 正常完成/取消/异常统一收尾（刷新检查点/元数据、释放库连接）；
                // 用 CancellationToken.None：中断后的落盘不应再被取消中断
                progress.SetState("正在写入元数据…");
                await store.FinalizeAsync(CancellationToken.None);
            }
        }

        /// <summary>下载单个瓦片（含存在性跳过与重试）</summary>
        private static async Task DownloadTileAsync(
            HttpClient client, ITileStore store, IDownloadProgress progress,
            DownloadSource source, string[] subdomains,
            int z, int x, int y, int retry, CancellationToken ct)
        {
            // 断点续传：已存在则跳过并计完成
            if (await store.TileExistsAsync(z, x, y, ct))
            {
                progress.ReportTileDone(z, x, y, skipped: true);
                return;
            }

            var url = TileUrlBuilder.BuildUrl(source.Url, subdomains, source.Key, z, x, y);

            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseContentRead, ct);

                    if (resp.StatusCode == HttpStatusCode.NotFound)
                    {
                        // 404 视为空瓦片：跳过但计完成
                        progress.ReportTileDone(z, x, y, skipped: false);
                        return;
                    }
                    if (!resp.IsSuccessStatusCode)
                    {
                        throw new Exception($"请求错误:{(int)resp.StatusCode} {resp.StatusCode}");
                    }

                    var data = await resp.Content.ReadAsByteArrayAsync(ct);
                    if (data.Length == 0)
                    {
                        // 空响应也计为完成，避免死循环
                        progress.ReportTileDone(z, x, y, skipped: false);
                        return;
                    }

                    await store.SaveTileAsync(z, x, y, data, ct);
                    progress.ReportTileDone(z, x, y, skipped: false);
                    return;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    if (attempt >= Math.Max(0, retry))
                    {
                        // 重试耗尽，报告错误（不计完成）
                        progress.ReportError(z, x, y, $"第{attempt + 1}次下载失败：{(e.InnerException ?? e).Message}");
                        return;
                    }
                    // 重试前短暂等待，缓解服务端限流
                    await Task.Delay(200 * (attempt + 1), ct);
                }
            }
        }
    }
}
