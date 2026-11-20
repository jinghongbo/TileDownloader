using System;
using System.IO;

namespace TileDownloader.Services
{
    /// <summary>
    /// 输出路径互斥判定（并发下载保护）：
    /// 同一路径、或一个路径位于另一个路径之下（目录任务与其内部文件）视为冲突；
    /// 同一目录下的兄弟文件、名字前缀相同的兄弟目录不冲突。
    /// </summary>
    public static class OutputPathGuard
    {
        /// <summary>规范化路径：绝对化 + 去掉结尾分隔符（盘符根目录保留为 "C:"）</summary>
        public static string Normalize(string path)
        {
            var full = Path.GetFullPath(path.Trim());
            return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        /// <summary>两个输出路径是否互斥</summary>
        public static bool Conflicts(string? a, string? b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
            {
                return false;
            }

            string x;
            string y;
            try
            {
                x = Normalize(a);
                y = Normalize(b);
            }
            catch
            {
                // 非法路径（含通配符等）：退化为字符串比较，避免抛异常打断下载
                return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
            }

            if (string.Equals(x, y, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return IsUnder(x, y) || IsUnder(y, x);
        }

        /// <summary>child 是否位于 parent 目录之下（按分隔符边界判断，避免 a / ab 误判）</summary>
        private static bool IsUnder(string child, string parent)
        {
            var prefix = parent.EndsWith(Path.DirectorySeparatorChar)
                ? parent.ToString()
                : parent + Path.DirectorySeparatorChar;
            return child.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
    }
}
