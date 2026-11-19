using System;

namespace TileDownloader.Models.Styles
{
    /// <summary>
    /// 样式文档：原生文本是唯一事实来源，不跨格式转换。
    /// </summary>
    public class MapStyleDocument
    {
        /// <summary>样式 Id（用户样式持久化用）</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("n");

        /// <summary>显示名称</summary>
        public string Name { get; set; } = "自定义样式";

        /// <summary>适配器 Id，如 google-apistyle</summary>
        public string ProviderId { get; set; } = "";

        /// <summary>原生文本（Google Style JSON / apistyle / 百度 JSON 等）</summary>
        public string Text { get; set; } = "";

        /// <summary>是否内置示例</summary>
        public bool IsBuiltIn { get; set; }
    }
}
