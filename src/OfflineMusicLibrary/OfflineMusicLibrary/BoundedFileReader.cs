using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace OfflineMusicLibrary;

internal static class ContentReadLimits
{
	public const int CacheMetadataBytes = 64 * 1024;
	public const int CachedLyricsBytes = 16 * 1024 * 1024;
	public const int ArtworkBytes = 32 * 1024 * 1024;
	public const int GenericCachePayloadBytes = 64 * 1024 * 1024;
	public const int LyricsFileBytes = 8 * 1024 * 1024;
	public const int HistoryImportBytes = 64 * 1024 * 1024;
	public const int PlaylistResponseBytes = 32 * 1024 * 1024;
	public const int TrackDetailResponseBytes = 8 * 1024 * 1024;
	public const int StateStringUtf8Bytes = 256 * 1024;
}

internal sealed class BoundedStringJsonConverter(int maximumUtf8Bytes) : JsonConverter<string>
{
	public override bool HandleNull => true;

	public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
	{
		if (reader.TokenType == JsonTokenType.Null)
		{
			return null;
		}
		if (reader.TokenType != JsonTokenType.String)
		{
			throw new JsonException("Expected a JSON string value.");
		}
		long encodedLength = reader.HasValueSequence ? reader.ValueSequence.Length : reader.ValueSpan.Length;
		if (encodedLength > maximumUtf8Bytes)
		{
			throw new JsonException($"State string exceeds the {maximumUtf8Bytes / 1024} KiB safety limit.");
		}
		return reader.GetString();
	}

	public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
	{
		if (value == null)
		{
			writer.WriteNullValue();
			return;
		}
		if (value.Length > maximumUtf8Bytes || Encoding.UTF8.GetByteCount(value) > maximumUtf8Bytes)
		{
			throw new JsonException($"State string exceeds the {maximumUtf8Bytes / 1024} KiB safety limit.");
		}
		writer.WriteStringValue(value);
	}
}

internal static class BoundedFileReader
{
	public static byte[] ReadAllBytes(string path, int maximumBytes)
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
		using FileStream stream = new(
			path,
			FileMode.Open,
			FileAccess.Read,
			FileShare.Read,
			64 * 1024,
			FileOptions.SequentialScan);
		long length = ValidateLength(stream, path, maximumBytes);
		byte[] bytes = new byte[(int)length];
		stream.ReadExactly(bytes);
		ValidateUnchangedLength(stream, path, length, maximumBytes);
		return bytes;
	}

	public static async Task<string> ReadAllTextAsync(
		string path,
		int maximumBytes,
		CancellationToken cancellationToken = default)
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
		await using FileStream stream = new(
			path,
			FileMode.Open,
			FileAccess.Read,
			FileShare.Read,
			64 * 1024,
			FileOptions.Asynchronous | FileOptions.SequentialScan);
		long length = ValidateLength(stream, path, maximumBytes);
		using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, 64 * 1024, leaveOpen: true);
		string text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
		ValidateUnchangedLength(stream, path, length, maximumBytes);
		return text;
	}

	private static long ValidateLength(FileStream stream, string path, int maximumBytes)
	{
		long length = stream.Length;
		if (length > maximumBytes)
		{
			throw new InvalidDataException($"File is too large to read safely ({length} bytes, limit {maximumBytes}): {Path.GetFileName(path)}");
		}
		return length;
	}

	private static void ValidateUnchangedLength(FileStream stream, string path, long expectedLength, int maximumBytes)
	{
		long currentLength = stream.Length;
		if (currentLength != expectedLength || currentLength > maximumBytes)
		{
			throw new InvalidDataException($"File changed while it was being read: {Path.GetFileName(path)}");
		}
	}
}
