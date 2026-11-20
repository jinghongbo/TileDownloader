using System;
using System.Threading;
using System.Threading.Tasks;
using TileDownloader.Services;
using Xunit;

namespace TileDownloader.Tests
{
    /// <summary>
    /// 全局并发闸门：所有任务共享同一个并发上限（原来是每任务各自 N 并发）
    /// </summary>
    public class GlobalDownloadLimiterTests
    {
        [Fact]
        public async Task 上限内的获取立即成功()
        {
            var limiter = new GlobalDownloadLimiter(2);
            using var a = await limiter.AcquireAsync(CancellationToken.None);
            using var b = await limiter.AcquireAsync(CancellationToken.None);
            Assert.Equal(2, limiter.InFlight);
            Assert.Equal(0, limiter.Waiting);
        }

        [Fact]
        public async Task 超出上限的获取会等待释放()
        {
            var limiter = new GlobalDownloadLimiter(2);
            var a = await limiter.AcquireAsync(CancellationToken.None);
            var b = await limiter.AcquireAsync(CancellationToken.None);

            var pending = limiter.AcquireAsync(CancellationToken.None).AsTask();
            await Task.Delay(50);
            Assert.False(pending.IsCompleted);
            Assert.Equal(1, limiter.Waiting);

            a.Dispose();
            using var c = await pending;
            Assert.Equal(2, limiter.InFlight);

            b.Dispose();
            c.Dispose();
            Assert.Equal(0, limiter.InFlight);
        }

        [Fact]
        public async Task 调大上限立即放行等待者()
        {
            var limiter = new GlobalDownloadLimiter(1);
            var a = await limiter.AcquireAsync(CancellationToken.None);
            var pending = limiter.AcquireAsync(CancellationToken.None).AsTask();
            await Task.Delay(30);
            Assert.False(pending.IsCompleted);

            limiter.SetLimit(2);
            using var b = await pending;
            Assert.Equal(2, limiter.InFlight);

            a.Dispose();
            b.Dispose();
            Assert.Equal(0, limiter.InFlight);
        }

        [Fact]
        public async Task 调小上限后在飞数收敛前不再放行()
        {
            var limiter = new GlobalDownloadLimiter(2);
            var a = await limiter.AcquireAsync(CancellationToken.None);
            var b = await limiter.AcquireAsync(CancellationToken.None);
            var pending = limiter.AcquireAsync(CancellationToken.None).AsTask();

            limiter.SetLimit(1);
            a.Dispose();
            await Task.Delay(50);
            Assert.False(pending.IsCompleted);

            b.Dispose();
            using var c = await pending;
            Assert.Equal(1, limiter.InFlight);
            c.Dispose();
            Assert.Equal(0, limiter.InFlight);
        }

        [Fact]
        public async Task 取消等待者会移除且不占名额()
        {
            var limiter = new GlobalDownloadLimiter(1);
            var a = await limiter.AcquireAsync(CancellationToken.None);
            using var cts = new CancellationTokenSource();
            var pending = limiter.AcquireAsync(cts.Token).AsTask();
            await Task.Delay(30);

            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.Equal(0, limiter.Waiting);
            Assert.Equal(1, limiter.InFlight);

            a.Dispose();
            Assert.Equal(0, limiter.InFlight);
        }

        [Fact]
        public void 上限至少为1()
        {
            var limiter = new GlobalDownloadLimiter(0);
            Assert.Equal(1, limiter.Limit);
            limiter.SetLimit(-5);
            Assert.Equal(1, limiter.Limit);
        }
    }
}
