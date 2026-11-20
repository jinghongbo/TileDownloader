using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using TileDownloader.Models;
using TileDownloader.Models.Styles;

namespace TileDownloader.Services.Styles
{
    /// <summary>
    /// Google 样式适配器：把 Google Style JSON（或整串 apistyle）编码成瓦片 URL 的 apistyle 参数。
    /// 官方样式 JSON 是唯一事实来源，校验只报告问题，不改写文本。
    /// </summary>
    public class GoogleStyleAdapter : IMapStyleAdapter
    {
        public string Id => "google-apistyle";

        public string DisplayName => "Google 样式";

        private static readonly Dictionary<string, string> Features = new(StringComparer.OrdinalIgnoreCase)
        {
            ["all"] = "0",
            ["administrative"] = "1",
            ["administrative.country"] = "17",
            ["administrative.province"] = "18",
            ["administrative.locality"] = "19",
            ["administrative.neighborhood"] = "20",
            ["administrative.land_parcel"] = "21",
            ["landscape"] = "5",
            ["landscape.man_made"] = "81",
            ["landscape.natural"] = "82",
            ["poi"] = "2",
            ["poi.attraction"] = "37",
            ["poi.business"] = "33",
            ["poi.government"] = "34",
            ["poi.medical"] = "36",
            ["poi.park"] = "40",
            ["poi.place_of_worship"] = "38",
            ["poi.school"] = "35",
            ["poi.sports_complex"] = "39",
            ["road"] = "3",
            ["road.arterial"] = "50",
            ["road.highway"] = "49",
            ["road.local"] = "51",
            ["transit"] = "4",
            ["transit.line"] = "65",
            ["transit.station"] = "66",
            ["water"] = "6",
        };

        private static readonly Dictionary<string, string> Elements = new(StringComparer.OrdinalIgnoreCase)
        {
            ["all"] = "a",
            ["geometry"] = "g",
            ["geometry.fill"] = "g.f",
            ["geometry.stroke"] = "g.s",
            ["labels"] = "l",
            ["labels.icon"] = "l.i",
            ["labels.text"] = "l.t",
            ["labels.text.fill"] = "l.t.f",
            ["labels.text.stroke"] = "l.t.s",
        };

        private static readonly Dictionary<string, string> Stylers = new(StringComparer.OrdinalIgnoreCase)
        {
            ["color"] = "p.c",
            ["visibility"] = "p.v",
            ["weight"] = "p.w",
            ["hue"] = "p.h",
            ["saturation"] = "p.s",
            ["lightness"] = "p.l",
            ["gamma"] = "p.g",
            ["invert_lightness"] = "p.il",
        };

        public bool Supports(DownloadSource source)
            => string.Equals(source?.StyleProvider, Id, StringComparison.OrdinalIgnoreCase);

        public StyleParseResult Parse(string text)
        {
            var raw = (text ?? string.Empty).Trim();
            if (raw.Length == 0)
            {
                return StyleParseResult.Success(0);
            }

            if (raw[0] == '[' || raw[0] == '{')
            {
                return ParseJson(raw);
            }

            return ParseApiStyle(raw);
        }

        private static StyleParseResult ParseApiStyle(string raw)
        {
            var rules = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (rules.Length == 0 || !rules.Any(r => r.Contains("s.t:", StringComparison.Ordinal) || r.Contains("p.", StringComparison.Ordinal)))
            {
                return StyleParseResult.Fail("无法识别的样式文本：既不是 Google Style JSON，也不像 apistyle 参数");
            }

            return StyleParseResult.Success(rules.Length);
        }

        private static StyleParseResult ParseJson(string raw)
        {
            JToken root;
            try
            {
                root = JToken.Parse(raw);
            }
            catch (Exception ex)
            {
                return StyleParseResult.Fail("JSON 解析失败：" + ex.Message);
            }

            if (root is not JArray array)
            {
                return StyleParseResult.Fail("Google 样式应为 JSON 数组，例如 [ { \"featureType\": \"water\", ... } ]");
            }

            var warnings = new List<string>();
            var count = 0;

            foreach (var item in array)
            {
                if (item is not JObject rule)
                {
                    warnings.Add("忽略了一条非对象规则");
                    continue;
                }

                if (rule["stylers"] is JObject)
                {
                    return StyleParseResult.Fail("检测到百度格式（stylers 为对象）。当前来源只支持 Google 样式（stylers 为数组）");
                }

                var feature = rule.Value<string>("featureType");
                var element = rule.Value<string>("elementType");

                if (!string.IsNullOrWhiteSpace(feature) && !Features.ContainsKey(feature))
                {
                    warnings.Add($"未知 featureType \"{feature}\"，整条规则已忽略");
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(element) && !Elements.ContainsKey(element))
                {
                    warnings.Add($"未知 elementType \"{element}\"，整条规则已忽略");
                    continue;
                }

                if (rule["stylers"] is not JArray stylers || stylers.Count == 0)
                {
                    warnings.Add("有一条规则没有 stylers，已忽略");
                    continue;
                }

                var valid = false;
                foreach (var stylerToken in stylers)
                {
                    if (stylerToken is not JObject styler)
                    {
                        continue;
                    }

                    foreach (var prop in styler.Properties())
                    {
                        if (!Stylers.ContainsKey(prop.Name))
                        {
                            warnings.Add($"未知样式属性 \"{prop.Name}\"，已忽略");
                            continue;
                        }

                        if (prop.Name.Equals("color", StringComparison.OrdinalIgnoreCase))
                        {
                            var color = prop.Value?.ToString() ?? string.Empty;
                            if (!Regex.IsMatch(color, "^#[0-9a-fA-F]{6}([0-9a-fA-F]{2})?$"))
                            {
                                warnings.Add($"颜色 \"{color}\" 不是 #RRGGBB / #AARRGGBB，已忽略");
                                continue;
                            }

                            if (color.Length == 9)
                            {
                                warnings.Add("8 位颜色按 #AARRGGBB 解释（与百度 #RRGGBBAA 相反）");
                            }
                        }

                        valid = true;
                    }
                }

                if (valid)
                {
                    count++;
                }
            }

            if (count == 0 && warnings.Count == 0)
            {
                return StyleParseResult.Fail("数组里没有可用的样式规则");
            }

            return StyleParseResult.Success(count, warnings);
        }

        /// <summary>JSON → apistyle token 串；失败返回 null</summary>
        private static string? BuildTokens(string raw, out List<string> warnings)
        {
            warnings = new List<string>();
            var array = JArray.Parse(raw);
            var rules = new List<string>();

            foreach (var item in array)
            {
                if (item is not JObject rule)
                {
                    continue;
                }

                var parts = new List<string>();
                var feature = rule.Value<string>("featureType");
                var element = rule.Value<string>("elementType");

                if (!string.IsNullOrWhiteSpace(feature))
                {
                    if (!Features.TryGetValue(feature, out var code))
                    {
                        continue;
                    }
                    parts.Add("s.t:" + code);
                }

                if (!string.IsNullOrWhiteSpace(element))
                {
                    if (!Elements.TryGetValue(element, out var code))
                    {
                        continue;
                    }
                    parts.Add("s.e:" + code);
                }

                if (rule["stylers"] is not JArray stylers)
                {
                    continue;
                }

                var hasStyler = false;
                foreach (var stylerToken in stylers.OfType<JObject>())
                {
                    foreach (var prop in stylerToken.Properties())
                    {
                        if (!Stylers.TryGetValue(prop.Name, out var key))
                        {
                            continue;
                        }

                        var value = prop.Value?.ToString() ?? string.Empty;
                        if (value.Length == 0)
                        {
                            continue;
                        }

                        parts.Add(key + ":" + value);
                        hasStyler = true;
                    }
                }

                if (hasStyler && parts.Count > 0)
                {
                    rules.Add(string.Join("|", parts));
                }
            }

            return rules.Count == 0 ? null : string.Join(",", rules);
        }

        public string Apply(DownloadSource source, string text)
        {
            var url = source?.Url ?? string.Empty;
            var cleaned = Regex.Replace(url, @"([&?])apistyle=[^&]*", string.Empty).TrimEnd('&');

            var raw = (text ?? string.Empty).Trim();
            if (raw.Length == 0)
            {
                return cleaned;
            }

            string? tokens;
            if (raw[0] == '[' || raw[0] == '{')
            {
                try
                {
                    tokens = BuildTokens(raw, out _);
                }
                catch
                {
                    return cleaned;
                }
            }
            else
            {
                tokens = raw;
            }

            if (string.IsNullOrWhiteSpace(tokens))
            {
                return cleaned;
            }

            return cleaned + "&apistyle=" + Uri.EscapeDataString(tokens);
        }

        public IReadOnlyList<MapStyleDocument> Presets { get; } = new List<MapStyleDocument>
        {
            new()
            {
                Name = "深蓝",
                ProviderId = "google-apistyle",
                IsBuiltIn = true,
                Text = Pretty(DeepBlueStyle),
            },
            new()
            {
                Name = "浅灰",
                ProviderId = "google-apistyle",
                IsBuiltIn = true,
                Text = Pretty(LightGrayStyle),
            },
        };

        /// <summary>内置样式以紧凑 JSON 常量保存，载入前统一缩进，编辑器里好读也好改。</summary>
        private static string Pretty(string json)
        {
            try
            {
                return JArray.Parse(json).ToString(Newtonsoft.Json.Formatting.Indented);
            }
            catch
            {
                return json;
            }
        }

        private const string DeepBlueStyle = """
            [
            {"featureType":"all","elementType":"labels.icon","stylers":[{"visibility":"off"}]},
            {"featureType":"all","elementType":"labels.text.fill","stylers":[{"color":"#A7C6DE"}]},
            {"featureType":"all","elementType":"labels.text.stroke","stylers":[{"color":"#050C16"},{"visibility":"on"}]},
            {"featureType":"landscape","elementType":"geometry.fill","stylers":[{"color":"#0A1424"}]},
            {"featureType":"landscape","elementType":"geometry.stroke","stylers":[{"color":"#08101C"}]},
            {"featureType":"landscape.natural","elementType":"geometry.fill","stylers":[{"color":"#0C1A2C"}]},
            {"featureType":"landscape.natural","elementType":"geometry.stroke","stylers":[{"color":"#0A1626"}]},
            {"featureType":"landscape.man_made","elementType":"geometry.fill","stylers":[{"color":"#101F35"}]},
            {"featureType":"landscape.man_made","elementType":"geometry.stroke","stylers":[{"color":"#0D1A2C"}]},
            {"featureType":"water","elementType":"geometry.fill","stylers":[{"color":"#0C2E4A"}]},
            {"featureType":"water","elementType":"geometry.stroke","stylers":[{"color":"#15506F"}]},
            {"featureType":"water","elementType":"labels.text.fill","stylers":[{"color":"#7FD3E6"}]},
            {"featureType":"poi","elementType":"geometry.fill","stylers":[{"color":"#122440"}]},
            {"featureType":"poi","elementType":"geometry.stroke","stylers":[{"color":"#0F1E33"}]},
            {"featureType":"poi.park","elementType":"geometry.fill","stylers":[{"color":"#0E2A30"}]},
            {"featureType":"poi.park","elementType":"geometry.stroke","stylers":[{"color":"#15414A"}]},
            {"featureType":"poi.park","elementType":"labels.text.fill","stylers":[{"color":"#4FB69F"}]},
            {"featureType":"poi.attraction","elementType":"geometry.fill","stylers":[{"color":"#1B2E4F"}]},
            {"featureType":"poi.attraction","elementType":"labels.text.fill","stylers":[{"color":"#D8B070"}]},
            {"featureType":"poi.business","elementType":"geometry.fill","stylers":[{"color":"#142845"}]},
            {"featureType":"poi.business","elementType":"labels.text.fill","stylers":[{"color":"#8FB4CE"}]},
            {"featureType":"poi.government","elementType":"geometry.fill","stylers":[{"color":"#152B47"}]},
            {"featureType":"poi.government","elementType":"labels.text.fill","stylers":[{"color":"#9FC0D8"}]},
            {"featureType":"poi.medical","elementType":"geometry.fill","stylers":[{"color":"#1A2C4C"}]},
            {"featureType":"poi.medical","elementType":"labels.text.fill","stylers":[{"color":"#D08C96"}]},
            {"featureType":"poi.school","elementType":"geometry.fill","stylers":[{"color":"#16294A"}]},
            {"featureType":"poi.school","elementType":"labels.text.fill","stylers":[{"color":"#A8C4D8"}]},
            {"featureType":"poi.place_of_worship","elementType":"geometry.fill","stylers":[{"color":"#182B4A"}]},
            {"featureType":"poi.place_of_worship","elementType":"labels.text.fill","stylers":[{"color":"#B3A6C8"}]},
            {"featureType":"poi.sports_complex","elementType":"geometry.fill","stylers":[{"color":"#0F2C3A"}]},
            {"featureType":"poi.sports_complex","elementType":"labels.text.fill","stylers":[{"color":"#7FD3E6"}]},
            {"featureType":"road","elementType":"geometry.fill","stylers":[{"color":"#182C45"}]},
            {"featureType":"road","elementType":"geometry.stroke","stylers":[{"color":"#0E1A2B"}]},
            {"featureType":"road.local","elementType":"geometry.fill","stylers":[{"color":"#172A42"}]},
            {"featureType":"road.local","elementType":"geometry.stroke","stylers":[{"color":"#0D1828"}]},
            {"featureType":"road.local","elementType":"labels.text.fill","stylers":[{"color":"#7E9FBA"}]},
            {"featureType":"road.arterial","elementType":"geometry.fill","stylers":[{"color":"#22405F"}]},
            {"featureType":"road.arterial","elementType":"geometry.stroke","stylers":[{"color":"#12243A"}]},
            {"featureType":"road.arterial","elementType":"labels.text.fill","stylers":[{"color":"#A7C6DE"}]},
            {"featureType":"road.highway","elementType":"geometry.fill","stylers":[{"color":"#2E5679"}]},
            {"featureType":"road.highway","elementType":"geometry.stroke","stylers":[{"color":"#16324D"}]},
            {"featureType":"road.highway","elementType":"labels.text.fill","stylers":[{"color":"#D8E6F2"}]},
            {"featureType":"road","elementType":"labels.icon","stylers":[{"visibility":"off"}]},
            {"featureType":"transit","elementType":"geometry.fill","stylers":[{"color":"#14283F"}]},
            {"featureType":"transit.line","elementType":"geometry.stroke","stylers":[{"color":"#1E3C58"}]},
            {"featureType":"transit.station","elementType":"geometry.fill","stylers":[{"color":"#16304A"}]},
            {"featureType":"transit","elementType":"labels.text.fill","stylers":[{"color":"#6E93B4"}]},
            {"featureType":"transit","elementType":"labels.icon","stylers":[{"visibility":"off"}]},
            {"featureType":"administrative","elementType":"geometry.stroke","stylers":[{"color":"#1B3A55"}]},
            {"featureType":"administrative.country","elementType":"geometry.stroke","stylers":[{"color":"#234A6B"},{"weight":2}]},
            {"featureType":"administrative.country","elementType":"labels.text.fill","stylers":[{"color":"#D6E7F5"}]},
            {"featureType":"administrative.province","elementType":"geometry.stroke","stylers":[{"color":"#1B3A55"},{"weight":1}]},
            {"featureType":"administrative.province","elementType":"labels.text.fill","stylers":[{"color":"#B8D2E6"}]},
            {"featureType":"administrative.locality","elementType":"labels.text.fill","stylers":[{"color":"#E2EFF9"}]},
            {"featureType":"administrative.neighborhood","elementType":"labels.text.fill","stylers":[{"color":"#8AA9C4"}]},
            {"featureType":"administrative.land_parcel","elementType":"geometry.fill","stylers":[{"color":"#0D1A2B"}]},
            {"featureType":"administrative.land_parcel","elementType":"geometry.stroke","stylers":[{"color":"#152C44"}]},
            {"featureType":"administrative.land_parcel","elementType":"labels","stylers":[{"visibility":"off"}]}
            ]
            """;

        private const string LightGrayStyle = """
            [
            {"featureType":"all","elementType":"labels.icon","stylers":[{"visibility":"off"}]},
            {"featureType":"all","elementType":"labels.text.fill","stylers":[{"color":"#46566A"}]},
            {"featureType":"all","elementType":"labels.text.stroke","stylers":[{"color":"#FFFFFF"},{"visibility":"on"}]},
            {"featureType":"landscape","elementType":"geometry.fill","stylers":[{"color":"#F2F4F7"}]},
            {"featureType":"landscape","elementType":"geometry.stroke","stylers":[{"color":"#E4E8EE"}]},
            {"featureType":"landscape.natural","elementType":"geometry.fill","stylers":[{"color":"#EDF2F4"}]},
            {"featureType":"landscape.natural","elementType":"geometry.stroke","stylers":[{"color":"#E0E7EA"}]},
            {"featureType":"landscape.man_made","elementType":"geometry.fill","stylers":[{"color":"#E7EDF3"}]},
            {"featureType":"landscape.man_made","elementType":"geometry.stroke","stylers":[{"color":"#D9E1EA"}]},
            {"featureType":"water","elementType":"geometry.fill","stylers":[{"color":"#D6E6F2"}]},
            {"featureType":"water","elementType":"geometry.stroke","stylers":[{"color":"#BBD4E6"}]},
            {"featureType":"water","elementType":"labels.text.fill","stylers":[{"color":"#3C7FA8"}]},
            {"featureType":"poi","elementType":"geometry.fill","stylers":[{"color":"#E9EEF4"}]},
            {"featureType":"poi","elementType":"geometry.stroke","stylers":[{"color":"#D9E1EA"}]},
            {"featureType":"poi.park","elementType":"geometry.fill","stylers":[{"color":"#DFEBD9"}]},
            {"featureType":"poi.park","elementType":"geometry.stroke","stylers":[{"color":"#C9DCC0"}]},
            {"featureType":"poi.park","elementType":"labels.text.fill","stylers":[{"color":"#4C7A4E"}]},
            {"featureType":"poi.attraction","elementType":"geometry.fill","stylers":[{"color":"#F4EADB"}]},
            {"featureType":"poi.attraction","elementType":"labels.text.fill","stylers":[{"color":"#9A6B3F"}]},
            {"featureType":"poi.business","elementType":"geometry.fill","stylers":[{"color":"#E8EDF4"}]},
            {"featureType":"poi.business","elementType":"labels.text.fill","stylers":[{"color":"#5B6B7C"}]},
            {"featureType":"poi.government","elementType":"geometry.fill","stylers":[{"color":"#E4EAF2"}]},
            {"featureType":"poi.government","elementType":"labels.text.fill","stylers":[{"color":"#4A617A"}]},
            {"featureType":"poi.medical","elementType":"geometry.fill","stylers":[{"color":"#F7E6E6"}]},
            {"featureType":"poi.medical","elementType":"labels.text.fill","stylers":[{"color":"#A05252"}]},
            {"featureType":"poi.school","elementType":"geometry.fill","stylers":[{"color":"#E8EFF7"}]},
            {"featureType":"poi.school","elementType":"labels.text.fill","stylers":[{"color":"#4E6E8E"}]},
            {"featureType":"poi.place_of_worship","elementType":"geometry.fill","stylers":[{"color":"#EFEAF3"}]},
            {"featureType":"poi.place_of_worship","elementType":"labels.text.fill","stylers":[{"color":"#705E86"}]},
            {"featureType":"poi.sports_complex","elementType":"geometry.fill","stylers":[{"color":"#E0EEEA"}]},
            {"featureType":"poi.sports_complex","elementType":"labels.text.fill","stylers":[{"color":"#3E7A72"}]},
            {"featureType":"road","elementType":"geometry.fill","stylers":[{"color":"#FFFFFF"}]},
            {"featureType":"road","elementType":"geometry.stroke","stylers":[{"color":"#E6EAEF"}]},
            {"featureType":"road.local","elementType":"geometry.fill","stylers":[{"color":"#FFFFFF"}]},
            {"featureType":"road.local","elementType":"geometry.stroke","stylers":[{"color":"#E3E8EE"}]},
            {"featureType":"road.local","elementType":"labels.text.fill","stylers":[{"color":"#6B7B8C"}]},
            {"featureType":"road.arterial","elementType":"geometry.fill","stylers":[{"color":"#FFFFFF"}]},
            {"featureType":"road.arterial","elementType":"geometry.stroke","stylers":[{"color":"#D6DEE8"}]},
            {"featureType":"road.arterial","elementType":"labels.text.fill","stylers":[{"color":"#56677A"}]},
            {"featureType":"road.highway","elementType":"geometry.fill","stylers":[{"color":"#F6DCA8"}]},
            {"featureType":"road.highway","elementType":"geometry.stroke","stylers":[{"color":"#E4C27E"}]},
            {"featureType":"road.highway","elementType":"labels.text.fill","stylers":[{"color":"#8A6A2F"}]},
            {"featureType":"road","elementType":"labels.icon","stylers":[{"visibility":"off"}]},
            {"featureType":"transit","elementType":"geometry.fill","stylers":[{"color":"#E4EBF2"}]},
            {"featureType":"transit.line","elementType":"geometry.stroke","stylers":[{"color":"#BFD0DF"}]},
            {"featureType":"transit.station","elementType":"geometry.fill","stylers":[{"color":"#FFFFFF"}]},
            {"featureType":"transit","elementType":"labels.text.fill","stylers":[{"color":"#6B7C8F"}]},
            {"featureType":"transit","elementType":"labels.icon","stylers":[{"visibility":"off"}]},
            {"featureType":"administrative","elementType":"geometry.stroke","stylers":[{"color":"#B6C3D1"}]},
            {"featureType":"administrative.country","elementType":"geometry.stroke","stylers":[{"color":"#9AAABD"},{"weight":2}]},
            {"featureType":"administrative.country","elementType":"labels.text.fill","stylers":[{"color":"#33455C"}]},
            {"featureType":"administrative.province","elementType":"geometry.stroke","stylers":[{"color":"#BFCBD9"},{"weight":1}]},
            {"featureType":"administrative.province","elementType":"labels.text.fill","stylers":[{"color":"#4A5D73"}]},
            {"featureType":"administrative.locality","elementType":"labels.text.fill","stylers":[{"color":"#26364A"}]},
            {"featureType":"administrative.neighborhood","elementType":"labels.text.fill","stylers":[{"color":"#5C6C7E"}]},
            {"featureType":"administrative.land_parcel","elementType":"geometry.fill","stylers":[{"color":"#F7F9FB"}]},
            {"featureType":"administrative.land_parcel","elementType":"geometry.stroke","stylers":[{"color":"#E8ECF1"}]},
            {"featureType":"administrative.land_parcel","elementType":"labels","stylers":[{"visibility":"off"}]}
            ]
            """;

        public string BuildAiPrompt(string scenario)
        {
            var requirement = scenario switch
            {
                "light" => "浅色调：米白底、浅蓝水域、深灰文字，干净明亮，适合白天和打印。",
                "contrast" => "高对比：接近纯黑的底、亮青色的道路、纯白文字，适合投影和车间大屏。",
                _ => "深色调：深夜蓝底、青色水域、薄荷绿文字，整体低饱和，适合夜间值班。",
            };

            var sb = new StringBuilder();
            sb.AppendLine("你是地图配色助手。只输出 JSON 数组，不要解释，不要 Markdown 代码块。");
            sb.AppendLine();
            sb.AppendLine("输出格式示例：");
            sb.AppendLine("[{\"featureType\":\"water\",\"elementType\":\"geometry\",\"stylers\":[{\"color\":\"#113549\"}]}]");
            sb.AppendLine();
            sb.AppendLine("featureType 只能取以下值之一：");
            sb.AppendLine("all, landscape, landscape.man_made, landscape.natural,");
            sb.AppendLine("water, poi, poi.park, poi.school, poi.medical, poi.attraction, poi.business,");
            sb.AppendLine("road, road.highway, road.arterial, road.local,");
            sb.AppendLine("transit, transit.line, transit.station,");
            sb.AppendLine("administrative, administrative.country, administrative.province,");
            sb.AppendLine("administrative.locality, administrative.neighborhood, administrative.land_parcel");
            sb.AppendLine();
            sb.AppendLine("elementType 只能取：geometry, geometry.fill, geometry.stroke, labels,");
            sb.AppendLine("labels.text, labels.text.fill, labels.text.stroke, labels.icon");
            sb.AppendLine();
            sb.AppendLine("stylers 只能使用：color（#RRGGBB）、visibility（on/off）、weight（1-8）");
            sb.AppendLine();
            sb.AppendLine("约束：最多 32 条规则；后写的规则覆盖先写的；颜色必须是 6 位十六进制。");
            sb.AppendLine();
            sb.Append("配色需求：").Append(requirement);
            return sb.ToString();
        }
    }
}
