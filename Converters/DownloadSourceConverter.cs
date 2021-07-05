using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using MapDownloader.Models;

namespace MapDownloader.Converters
{
    public class DownloadSourceConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType)
        {
            return objectType == typeof(DownloadSource);
        }

        public override bool CanWrite { get { return false; } }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            var token = JToken.Load(reader);
            var typeToken = token["Type"];
            if (typeToken == null)
                typeToken = "Tile";
            Type actualType = typeToken.ToObject<string>(serializer) switch
            {
                "Tile" => typeof(TileDownloadSource),
                "Tianditu" => typeof(TiandituDownloadSource),
                "GSCloud" => typeof(GSCloudDownloadSource),
                _ => throw new NotSupportedException("不支持"),
            };
            if (existingValue == null || existingValue.GetType() != actualType)
            {
                var contract = serializer.ContractResolver.ResolveContract(actualType);
                existingValue = contract.DefaultCreator();
            }
            using (var subReader = token.CreateReader())
            {
                serializer.Populate(subReader, existingValue);
            }
            return existingValue;
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            throw new NotImplementedException();
        }
    }
}