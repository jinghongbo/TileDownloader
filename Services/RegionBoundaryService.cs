using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NetTopologySuite.Geometries;
using Newtonsoft.Json.Linq;

namespace TileDownloader.Services
{
    /// <summary>
    /// 行政区边界服务：按 adcode 拉取 GeoJSON 多边形并缓存到本机。
    /// 数据源：阿里 DataV 行政区划边界（与 regions.json 的 adcode 一致）。
    /// </summary>
    public class RegionBoundaryService
    {
        private const string Endpoint = "https://geo.datav.aliyun.com/areas_v3/bound/{0}.json";

        private readonly HttpClient _http;
        private readonly string _cacheDir;
        private readonly GeometryFactory _factory = new();

        public RegionBoundaryService()
        {
            _cacheDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TileDownloader",
                "boundaries");

            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            _http.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36");
        }

        /// <summary>取行政区边界；失败返回 null（调用方退回外包框）</summary>
        public async Task<Geometry?> GetBoundaryAsync(string? adcode, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(adcode))
            {
                return null;
            }

            var safeCode = new string(Array.FindAll(adcode.ToCharArray(), char.IsLetterOrDigit));
            if (safeCode.Length == 0)
            {
                return null;
            }

            var file = Path.Combine(_cacheDir, safeCode + ".json");
            try
            {
                if (File.Exists(file))
                {
                    var cached = await File.ReadAllTextAsync(file, ct);
                    var geometry = ParseGeoJson(cached, _factory);
                    if (geometry != null)
                    {
                        return geometry;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // 缓存损坏：继续走网络
            }

            try
            {
                var json = await _http.GetStringAsync(string.Format(Endpoint, safeCode), ct);
                var geometry = ParseGeoJson(json, _factory);
                if (geometry != null)
                {
                    Directory.CreateDirectory(_cacheDir);
                    await File.WriteAllTextAsync(file, json, ct);
                }
                return geometry;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>解析 DataV GeoJSON（取第一个要素的 Polygon/MultiPolygon）</summary>
        public static Geometry? ParseGeoJson(string json, GeometryFactory? factory = null)
        {
            try
            {
                var root = JObject.Parse(json);
                if (root["features"] is not JArray features || features.Count == 0)
                {
                    return null;
                }

                return features[0]?["geometry"] is JObject geometry
                    ? ToGeometry(geometry, factory ?? new GeometryFactory())
                    : null;
            }
            catch
            {
                return null;
            }
        }

        private static Geometry? ToGeometry(JObject geometry, GeometryFactory factory)
        {
            var type = geometry.Value<string>("type");
            var coordinates = geometry["coordinates"] as JArray;
            if (coordinates == null)
            {
                return null;
            }

            switch (type)
            {
                case "Polygon":
                    return ReadPolygon(coordinates, factory);

                case "MultiPolygon":
                {
                    var polygons = new List<Polygon>();
                    foreach (var item in coordinates)
                    {
                        if (item is JArray polygon)
                        {
                            var p = ReadPolygon(polygon, factory);
                            if (p != null)
                            {
                                polygons.Add(p);
                            }
                        }
                    }

                    return polygons.Count switch
                    {
                        0 => null,
                        1 => polygons[0],
                        _ => factory.CreateMultiPolygon(polygons.ToArray()),
                    };
                }

                default:
                    return null;
            }
        }

        private static Polygon? ReadPolygon(JArray rings, GeometryFactory factory)
        {
            if (rings.Count == 0)
            {
                return null;
            }

            var shell = ReadRing(rings[0] as JArray, factory);
            if (shell == null)
            {
                return null;
            }

            var holes = new List<LinearRing>();
            for (var i = 1; i < rings.Count; i++)
            {
                var hole = ReadRing(rings[i] as JArray, factory);
                if (hole != null)
                {
                    holes.Add(hole);
                }
            }

            return factory.CreatePolygon(shell, holes.ToArray());
        }

        private static LinearRing? ReadRing(JArray? positions, GeometryFactory factory)
        {
            if (positions == null || positions.Count < 3)
            {
                return null;
            }

            var coords = new List<Coordinate>(positions.Count + 1);
            foreach (var position in positions)
            {
                if (position is not JArray pair || pair.Count < 2)
                {
                    continue;
                }

                coords.Add(new Coordinate(pair[0].Value<double>(), pair[1].Value<double>()));
            }

            if (coords.Count < 3)
            {
                return null;
            }

            if (!coords[0].Equals2D(coords[^1]))
            {
                coords.Add(new Coordinate(coords[0]));
            }

            return factory.CreateLinearRing(coords.ToArray());
        }
    }
}
