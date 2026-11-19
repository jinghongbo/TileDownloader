using System.Collections.Generic;
using TileDownloader.Models;
using TileDownloader.Models.Styles;

namespace TileDownloader.Services.Styles
{
    /// <summary>
    /// 样式适配器：校验文本、把样式应用到 URL、提供示例与 AI 格式说明。
    /// 每个地图源只使用自己的原生格式，不做跨格式转换。
    /// </summary>
    public interface IMapStyleAdapter
    {
        /// <summary>适配器 Id，写入 DownloadSource.StyleProvider</summary>
        string Id { get; }

        /// <summary>显示名称</summary>
        string DisplayName { get; }

        /// <summary>该来源是否由本适配器负责</summary>
        bool Supports(DownloadSource source);

        /// <summary>校验文本（只读，不改写）</summary>
        StyleParseResult Parse(string text);

        /// <summary>把样式应用到来源 URL，返回新 URL（不修改入参）</summary>
        string Apply(DownloadSource source, string text);

        /// <summary>内置示例（文本）</summary>
        IReadOnlyList<MapStyleDocument> Presets { get; }

        /// <summary>生成可直接发给 AI 的提示词</summary>
        string BuildAiPrompt(string scenario);
    }
}
