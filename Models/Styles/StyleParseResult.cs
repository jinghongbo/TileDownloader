using System;
using System.Collections.Generic;
using System.Linq;

namespace TileDownloader.Models.Styles
{
    /// <summary>样式文本校验结果：只报告问题，不改写原文。</summary>
    public class StyleParseResult
    {
        public bool Ok { get; init; }

        /// <summary>失败原因（Ok=false 时非空）</summary>
        public string? Error { get; init; }

        /// <summary>可识别的规则条数</summary>
        public int RuleCount { get; init; }

        /// <summary>不阻断使用的提示（未知要素、被忽略的属性等）</summary>
        public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

        public static StyleParseResult Success(int ruleCount, IEnumerable<string>? warnings = null)
            => new() { Ok = true, RuleCount = ruleCount, Warnings = warnings?.ToList() ?? new List<string>() };

        public static StyleParseResult Fail(string error)
            => new() { Ok = false, Error = error };
    }
}
