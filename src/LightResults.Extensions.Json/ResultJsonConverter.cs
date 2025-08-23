using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LightResults.Extensions.Json;

/// <summary>Converts a <see cref="Result"/> to JSON.</summary>
/// <remarks>This converter only supports serialization.</remarks>
/// <seealso cref="JsonConverter{T}"/>
[SuppressMessage("Design", "CA1062: Validate arguments of public methods", Justification = "Arguments are provided by the SDK.")]
public sealed class ResultJsonConverter : JsonConverter<Result>
{
    private const string TypeDiscriminator = "$type";
    private const string IsSuccess = "IsSuccess";
    private const string Errors = "Errors";
    private const string Message = "Message";
    private const string Metadata = "Metadata";
    private const string MetadataValue = "Value";
    private const string ExceptionMessage = "Message";
    private const string ExceptionStackTrace = "StackTrace";
    private const string ExceptionInnerException = "InnerException";

    /// <summary>Reads and converts JSON to a <see cref="Result"/> object.</summary>
    /// <param name="reader">The JSON reader.</param>
    /// <param name="typeToConvert">The type of the object to convert.</param>
    /// <param name="options">The serialization options to use.</param>
    /// <returns>The deserialized <see cref="Result"/> object.</returns>
    /// <exception cref="NotImplementedException">Thrown when the method is called as it's not implemented.</exception>
    public override Result Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Expected StartObject token");

        bool? isSuccess = null;
        List<IError>? errors = null;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                break;

            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException("Expected PropertyName token");

            string propertyName = reader.GetString()!;
            reader.Read();

            switch (propertyName)
            {
                case IsSuccess:
                    isSuccess = reader.GetBoolean();
                    break;
                case Errors:
                    if (reader.TokenType == JsonTokenType.StartArray)
                    {
                        var errorList = new List<IError>();
                        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                        {
                            errorList.Add(ReadError(ref reader, options));
                        }
                        errors = errorList.Count > 0 ? errorList : null;
                    }
                    break;
            }
        }

        if (isSuccess == null)
            throw new JsonException("Missing IsSuccess property");

        if (isSuccess.Value)
            return Result.Success();
        else
            return Result.Failure(errors ?? new List<IError>());
    }

    private static IError ReadError(ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
        string? message = null;
        Dictionary<string, object?> metadata = new();

        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Expected StartObject token for error");

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                break;

            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException("Expected PropertyName token in error object");

            string propertyName = reader.GetString()!;
            reader.Read();

            switch (propertyName)
            {
                case Message:
                    message = reader.GetString();
                    break;
                case Metadata:
                    if (reader.TokenType == JsonTokenType.StartObject)
                    {
                        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                        {
                            if (reader.TokenType == JsonTokenType.PropertyName)
                            {
                                string key = reader.GetString()!;
                                reader.Read();
                                metadata[key] = JsonSerializer.Deserialize<object?>(ref reader, options);
                            }
                        }
                    }
                    break;
            }
        }

        if (message == null)
            throw new JsonException("Missing Message property in error object");

        // Try to create Error, fallback to a basic implementation if not available
        var errorType = Type.GetType("LightResults.Error") ?? typeof(BasicError);
        return (IError)Activator.CreateInstance(errorType, message, metadata)!;
    }

    private class BasicError : IError
    {
        public string Message { get; }
        public IReadOnlyDictionary<string, object?> Metadata { get; }
        public Exception? Exception => null;
        public BasicError(string message, Dictionary<string, object?>? metadata = null)
        {
            Message = message;
            Metadata = metadata ?? new Dictionary<string, object?>();
        }
    }

    /// <summary>Writes a <see cref="Result"/> object to JSON.</summary>
    /// <param name="writer">The JSON writer.</param>
    /// <param name="value">The <see cref="Result"/> object to write.</param>
    /// <param name="options">The serialization options to use.</param>
    public override void Write(Utf8JsonWriter writer, Result value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        if (value.IsSuccess())
        {
            writer.WriteBoolean(IsSuccess, true);
            
        }
        else
        {
            writer.WriteBoolean(IsSuccess, false);
            WriteErrors(writer, value);
        }
        writer.WriteEndObject();
    }

    private static void WriteErrors(Utf8JsonWriter writer, Result result)
    {
        writer.WritePropertyName(Errors);
        writer.WriteStartArray();
        foreach (var error in result.Errors)
            WriteError(writer, error);
        writer.WriteEndArray();
    }

    private static void WriteError(Utf8JsonWriter writer, IError error)
    {
        writer.WriteStartObject();
        writer.WriteString(TypeDiscriminator, error.GetType()
                                                  .FullName
                                              ?? error.GetType()
                                                  .Name
        );
        writer.WriteString(Message, error.Message);
        if (error.Metadata.Count > 0)
            WriteMetadata(writer, error);
        writer.WriteEndObject();
    }

    private static void WriteMetadata(Utf8JsonWriter writer, IError error)
    {
        writer.WritePropertyName(Metadata);
        writer.WriteStartObject();

        var keys = error.Metadata.Keys.OrderBy(x => x, StringComparer.InvariantCulture);
        foreach (var key in keys)
            WriteMetadataItem(writer, key, error.Metadata[key]);
        writer.WriteEndObject();
    }

    private static void WriteMetadataItem(Utf8JsonWriter writer, string key, object? obj)
    {
        if (obj is null)
        {
            writer.WritePropertyName(key);
            writer.WriteNullValue();
            return;
        }

        if (obj is Exception ex)
        {
            writer.WritePropertyName(key);
            WriteExceptionValue(writer, ex);
            return;
        }

        writer.WritePropertyName(key);
        writer.WriteStartObject();
        writer.WriteString(TypeDiscriminator, obj.GetType()
                                                  .FullName
                                              ?? obj.GetType()
                                                  .Name
        );
        WriteObject(writer, MetadataValue, obj);
        writer.WriteEndObject();
    }

    private static void WriteObject(Utf8JsonWriter writer, string name, object? obj)
    {
        if (obj is null)
        {
            writer.WriteNull(name);
            return;
        }

        switch (obj)
        {
            case bool value:
                writer.WriteBoolean(name, value);
                break;
            case byte value:
                writer.WriteNumber(name, value);
                break;
            case DateTime value:
                writer.WriteString(name, value);
                break;
            case DateTimeOffset value:
                writer.WriteString(name, value);
                break;
            case DateOnly value:
                var dateOnlyValue = value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                writer.WriteString(name, dateOnlyValue);
                break;
            case TimeOnly value:
                var timeOnlyValue = value.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
                writer.WriteString(name, timeOnlyValue);
                break;
            case TimeSpan value:
                writer.WritePropertyName(name);
                writer.WriteNumberValue(value.Ticks);
                break;
            case double value:
                writer.WriteNumber(name, value);
                break;
            case Guid value:
                writer.WriteString(name, value);
                break;
            case short value:
                writer.WriteNumber(name, value);
                break;
            case int value:
                writer.WriteNumber(name, value);
                break;
            case long value:
                writer.WriteNumber(name, value);
                break;
            case sbyte value:
                writer.WriteNumber(name, value);
                break;
            case float value:
                writer.WriteNumber(name, value);
                break;
            case string value:
                writer.WriteString(name, value);
                break;
            case ushort value:
                writer.WriteNumber(name, value);
                break;
            case uint value:
                writer.WriteNumber(name, value);
                break;
            case ulong value:
                writer.WriteNumber(name, value);
                break;
#pragma warning disable IL2026
#pragma warning disable IL3050
#pragma warning disable CA1031
            default:
                try
                {
                    var json = JsonSerializer.Serialize(obj, obj.GetType());
                    writer.WritePropertyName(name);
                    writer.WriteRawValue(json);
                }
                catch
                {
                    var typeName = obj.GetType()
                                       .FullName
                                   ?? obj.GetType()
                                       .Name;
                    writer.WriteString(name, typeName);
                }
                break;
#pragma warning restore IL3050
#pragma warning restore IL2026
#pragma warning restore CA1031
        }
    }

    private static void WriteExceptionValue(Utf8JsonWriter writer, Exception ex)
    {
        writer.WriteStartObject();
        writer.WriteString(TypeDiscriminator, ex.GetType()
                                                  .FullName
                                              ?? ex.GetType()
                                                  .Name
        );
        writer.WriteString(ExceptionMessage, ex.Message);
        writer.WriteString(ExceptionStackTrace, ex.StackTrace);
        if (ex.InnerException is not null)
        {
            writer.WritePropertyName(ExceptionInnerException);
            WriteExceptionValue(writer, ex.InnerException);
        }
        writer.WriteEndObject();
    }
}
