using System.Linq;
using TileDownloader.Services.Styles;
using Xunit;

namespace TileDownloader.Tests
{
    /// <summary>
    /// 内置地图样式：默认只保留「原生 / 深蓝 / 浅灰」三套。
    /// 「原生」由界面附加为空样式文本，不出现在适配器的内置示例里。
    /// </summary>
    public class MapStylePresetTests
    {
        [Fact]
        public void 内置示例只有深蓝和浅灰()
        {
            var adapter = new GoogleStyleAdapter();

            Assert.Equal(new[] { "深蓝", "浅灰" }, adapter.Presets.Select(p => p.Name).ToArray());
        }

        [Fact]
        public void 内置示例都能通过自身校验()
        {
            var adapter = new GoogleStyleAdapter();

            foreach (var preset in adapter.Presets)
            {
                Assert.True(adapter.Parse(preset.Text).Ok, preset.Name + " 校验失败");
            }
        }
    }
}
