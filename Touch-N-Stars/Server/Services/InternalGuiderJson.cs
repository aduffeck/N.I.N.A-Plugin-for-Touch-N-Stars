using System;
using System.Linq;
using NINA.Equipment.Equipment.MyGuider.Advanced;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace TouchNStars.Server.Services;

/// <summary>
/// JSON conventions of the internal guider API and WebSocket: camelCase property names (the
/// IAdvancedGuider DTOs are PascalCase), UTC ISO timestamps, and NaN/Infinity written as null
/// (Newtonsoft would otherwise emit the bare token NaN, which JSON.parse rejects).
/// </summary>
public static class InternalGuiderJson
{
    public static readonly JsonSerializerSettings Settings = CreateSettings();

    private static JsonSerializerSettings CreateSettings()
    {
        var settings = new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            NullValueHandling = NullValueHandling.Include,
            DateTimeZoneHandling = DateTimeZoneHandling.Utc,
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
            Formatting = Formatting.None
        };
        settings.Converters.Add(new NonFiniteDoubleConverter());
        return settings;
    }

    public static string Serialize(object value) => JsonConvert.SerializeObject(value, Settings);

    /// <summary>The WebSocket envelope: { type, timestamp, payload }.</summary>
    public static string Envelope(string type, DateTime timestamp, object payload)
    {
        return Serialize(new
        {
            type,
            timestamp = timestamp.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(timestamp, DateTimeKind.Utc) : timestamp,
            payload = SanitizePayload(payload)
        });
    }

    /// <summary>
    /// Frames are never pushed with their pixels: a 'frame' event only tells clients that a new
    /// frame exists, they fetch /api/internal-guider/image and /frame-info themselves.
    /// </summary>
    private static object SanitizePayload(object payload)
    {
        return payload is AdvancedGuiderFrame frame ? FrameSummary(frame) : payload;
    }

    private static object FrameSummary(AdvancedGuiderFrame frame)
    {
        if (frame == null) return null;
        var stars = frame.Stars;
        return new
        {
            frameNumber = frame.FrameNumber,
            timestamp = frame.Timestamp,
            width = frame.Width,
            height = frame.Height,
            starCount = stars?.Count ?? 0,
            starsUsed = stars?.Count(s => s != null && s.Used) ?? 0
        };
    }

    /// <summary>Writes NaN and +/-Infinity as null for double and double? properties.</summary>
    public sealed class NonFiniteDoubleConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType) => objectType == typeof(double) || objectType == typeof(double?);

        public override bool CanRead => false;

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
            => throw new NotSupportedException();

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            if (value is double d && !double.IsNaN(d) && !double.IsInfinity(d))
            {
                writer.WriteValue(d);
            }
            else
            {
                writer.WriteNull();
            }
        }
    }
}
