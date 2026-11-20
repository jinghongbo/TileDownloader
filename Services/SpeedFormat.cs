using System;

namespace TileDownloader.Services
{
    /// <summary>
    /// 下载速度文本格式化（任务中心展示实际带宽）
    /// </summary>
    public static class SpeedFormat
    {
        /// <summary>字节/秒 → 人类可读网速（B/s、KB/s、MB/s）</summary>
        public static string BytesPerSecond(double bytesPerSecond)
        {
            if (bytesPerSecond <= 0)
            {
                return "--";
            }

            if (bytesPerSecond < 1024)
            {
                return $"{bytesPerSecond:F0} B/s";
            }

            if (bytesPerSecond < 1024 * 1024)
            {
                return $"{bytesPerSecond / 1024:F1} KB/s";
            }

            return $"{bytesPerSecond / (1024 * 1024):F1} MB/s";
        }
    }
}
