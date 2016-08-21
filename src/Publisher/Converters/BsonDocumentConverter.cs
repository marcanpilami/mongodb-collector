using MongoDB.Bson;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace admin.Converters
{
    public class BsonDocumentConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType)
        {
            return objectType != null && typeof(BsonValue).IsAssignableFrom(objectType);
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            throw new NotImplementedException();
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            if (value == null)
            {
                return;
            }

            var a = (BsonValue)value;
            var settings = new MongoDB.Bson.IO.JsonWriterSettings
            {
                OutputMode = MongoDB.Bson.IO.JsonOutputMode.Strict
            };

            if (a.IsBsonDocument)
            {
                writer.WriteStartObject();
                var res = a.ToJson(settings);
                writer.WriteRaw(res.Substring(1, res.Length - 2));
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteValue(a.ToString());
            }
        }
    }
}
