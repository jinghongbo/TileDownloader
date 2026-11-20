using System;
using System.IO;
using TileDownloader.Services;
using Xunit;

namespace TileDownloader.Tests
{
    /// <summary>
    /// 输出路径互斥判定：并发下载时同一路径（或目录与其内部文件）必须判为冲突
    /// </summary>
    public class OutputPathGuardTests
    {
        private static string P(params string[] parts) => Path.Combine(parts);

        [Fact]
        public void 相同绝对路径_冲突()
        {
            Assert.True(OutputPathGuard.Conflicts(P("D:", "maps", "map.pak"), P("D:", "maps", "map.pak")));
        }

        [Fact]
        public void 相对路径与绝对路径指向同一文件_冲突()
        {
            var relative = P("maps", "map.pak");
            var absolute = Path.GetFullPath(relative);
            Assert.True(OutputPathGuard.Conflicts(relative, absolute));
        }

        [Fact]
        public void 大小写不同_冲突()
        {
            Assert.True(OutputPathGuard.Conflicts(P("D:", "Maps", "Map.PAK"), P("D:", "maps", "map.pak")));
        }

        [Fact]
        public void 尾部目录分隔符不同_冲突()
        {
            Assert.True(OutputPathGuard.Conflicts(P("D:", "maps", "tiles") + Path.DirectorySeparatorChar, P("D:", "maps", "tiles")));
        }

        [Fact]
        public void 目录任务与目录内文件_冲突()
        {
            Assert.True(OutputPathGuard.Conflicts(P("D:", "maps"), P("D:", "maps", "map.pak")));
            Assert.True(OutputPathGuard.Conflicts(P("D:", "maps", "map.pak"), P("D:", "maps")));
        }

        [Fact]
        public void 父目录与子目录_冲突()
        {
            Assert.True(OutputPathGuard.Conflicts(P("D:", "maps"), P("D:", "maps", "sub")));
        }

        [Fact]
        public void 同一目录下的兄弟文件_不冲突()
        {
            Assert.False(OutputPathGuard.Conflicts(P("D:", "maps", "a.pak"), P("D:", "maps", "b.pak")));
        }

        [Fact]
        public void 名字前缀相同的兄弟目录_不冲突()
        {
            Assert.False(OutputPathGuard.Conflicts(P("D:", "maps", "a"), P("D:", "maps", "ab")));
        }

        [Fact]
        public void 盘符根目录与其中文件_冲突()
        {
            Assert.True(OutputPathGuard.Conflicts("C:" + Path.DirectorySeparatorChar, P("C:", "maps", "map.pak")));
        }

        [Fact]
        public void 空路径_不冲突()
        {
            Assert.False(OutputPathGuard.Conflicts("", P("D:", "maps")));
            Assert.False(OutputPathGuard.Conflicts(null!, P("D:", "maps")));
        }
    }
}
