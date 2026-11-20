using TileDownloader.Services;
using Xunit;

namespace TileDownloader.Tests
{
    /// <summary>
    /// 网速文本格式化：任务中心按字节/秒展示实际下载带宽
    /// </summary>
    public class SpeedFormatTests
    {
        [Fact]
        public void 无数据时显示占位符()
        {
            Assert.Equal("--", SpeedFormat.BytesPerSecond(0));
            Assert.Equal("--", SpeedFormat.BytesPerSecond(-1));
        }

        [Fact]
        public void 小于1KB按字节显示()
        {
            Assert.Equal("512 B/s", SpeedFormat.BytesPerSecond(512));
            Assert.Equal("1023 B/s", SpeedFormat.BytesPerSecond(1023));
        }

        [Fact]
        public void 千字节级别保留一位小数()
        {
            Assert.Equal("1.0 KB/s", SpeedFormat.BytesPerSecond(1024));
            Assert.Equal("1.5 KB/s", SpeedFormat.BytesPerSecond(1536));
        }

        [Fact]
        public void 兆字节级别保留一位小数()
        {
            Assert.Equal("1.0 MB/s", SpeedFormat.BytesPerSecond(1024 * 1024));
            Assert.Equal("5.5 MB/s", SpeedFormat.BytesPerSecond(5.5 * 1024 * 1024));
        }
    }
}
