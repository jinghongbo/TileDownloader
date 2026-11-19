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
                Name = "深夜蓝",
                ProviderId = "google-apistyle",
                IsBuiltIn = true,
                Text = BuildJson(array =>
                {
                    Add(array, "landscape", "geometry", ("color", "#091220"));
                    Add(array, "water", "geometry", ("color", "#113549"));
                    Add(array, "poi.park", "geometry", ("color", "#0e1b30"));
                    Add(array, "landscape.man_made", "geometry", ("color", "#0f1c30"));
                    Add(array, "poi", "geometry", ("color", "#101f35"));
                    Add(array, "road", "geometry", ("color", "#1b3350"));
                    Add(array, "road.highway", "geometry", ("color", "#2a4a6a"));
                    Add(array, "road.arterial", "geometry", ("color", "#23405f"));
                    Add(array, "road.local", "geometry", ("color", "#1b3350"));
                    Add(array, "transit", "geometry", ("visibility", "off"));
                    Add(array, "all", "labels.text.fill", ("color", "#2dc4bb"));
                    Add(array, "all", "labels.text.stroke", ("visibility", "off"));
                    Add(array, "all", "labels.icon", ("visibility", "off"));
                    Add(array, "road", "labels", ("visibility", "off"));
                }),
            },
            new()
            {
                Name = "极简灰",
                ProviderId = "google-apistyle",
                IsBuiltIn = true,
                Text = BuildJson(array =>
                {
                    Add(array, "landscape", "geometry", ("color", "#f3f4f6"));
                    Add(array, "water", "geometry", ("color", "#d9e8f2"));
                    Add(array, "poi.park", "geometry", ("color", "#e2ebdd"));
                    Add(array, "road", "geometry", ("color", "#ffffff"));
                    Add(array, "road.highway", "geometry", ("color", "#f3c96b"));
                    Add(array, "all", "labels.text.fill", ("color", "#4b5563"));
                    Add(array, "all", "labels.icon", ("visibility", "off"));
                }),
            },
        };

        private static void Add(JArray array, string featureType, string elementType, params (string Key, object Value)[] stylers)
        {
            var stylerObject = new JObject();
            foreach (var (key, value) in stylers)
            {
                stylerObject[key] = JToken.FromObject(value);
            }

            array.Add(new JObject
            {
                ["featureType"] = featureType,
                ["elementType"] = elementType,
                ["stylers"] = new JArray(stylerObject),
            });
        }

        private static string BuildJson(Action<JArray> build)
        {
            var array = new JArray();
            build(array);
            return array.ToString(Newtonsoft.Json.Formatting.Indented);
        }

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
