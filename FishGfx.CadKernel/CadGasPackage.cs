using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace FishGfx.Cad;

public sealed record CadPatchMatchingPolicy(
	int Version,
	double AreaAbsoluteTolerance,
	double AreaRelativeTolerance,
	double CentroidToleranceMm,
	double NormalAngularToleranceDegrees,
	double PerimeterRelativeTolerance,
	double LoopSampleToleranceMm,
	double UniqueScoreMargin
)
{
	public static CadPatchMatchingPolicy Version1 { get; } = new(
		1,
		0.0001,
		0.00001,
		0.01,
		0.1,
		0.00001,
		0.02,
		0.10
	);
}

public sealed record CadGasPackageInfo(
	string Path,
	string PackageFileHash,
	string GeometryStepHash,
	string CanonicalManifestHash
);

internal static class CadCanonicalJson
{
	internal static byte[] Serialize(JsonNode node)
	{
		using MemoryStream output = new();
		using Utf8JsonWriter writer = new(output, new JsonWriterOptions
		{
			Indented = false,
			SkipValidation = false,
		});
		WriteNode(writer, node);
		writer.Flush();
		return output.ToArray();
	}

	private static void WriteNode(Utf8JsonWriter writer, JsonNode node)
	{
		switch (node)
		{
			case JsonObject value:
				writer.WriteStartObject();
				foreach ((string name, JsonNode child) in value.OrderBy(
					item => item.Key,
					StringComparer.Ordinal))
				{
					writer.WritePropertyName(name);
					if (child == null) writer.WriteNullValue();
					else WriteNode(writer, child);
				}
				writer.WriteEndObject();
				break;
			case JsonArray value:
				writer.WriteStartArray();
				foreach (JsonNode child in value)
				{
					if (child == null) writer.WriteNullValue();
					else WriteNode(writer, child);
				}
				writer.WriteEndArray();
				break;
			case JsonValue value:
				value.WriteTo(writer);
				break;
			default:
				writer.WriteNullValue();
				break;
		}
	}
}

internal static class CadGasPackageWriter
{
	private static readonly DateTimeOffset FixedZipTime =
		new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

	internal static CadGasPackageInfo Write(
		string destinationPath,
		string stepPath,
		string nativeManifestJson)
	{
		string fullPath = Path.GetFullPath(destinationPath);
		string directory = Path.GetDirectoryName(fullPath)
			?? throw new ArgumentException("The package path has no parent directory.", nameof(destinationPath));
		Directory.CreateDirectory(directory);
		string token = Guid.NewGuid().ToString("N");
		string temporaryPackage = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{token}.tmp");
		string backup = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{token}.bak");
		try
		{
			byte[] geometry = NormalizeStep(File.ReadAllBytes(stepPath));
			string geometryHash = Hash(geometry);
			JsonObject manifest = JsonNode.Parse(nativeManifestJson)?.AsObject()
				?? throw new InvalidDataException("The native gas manifest is not a JSON object.");
			manifest["geometrySha256"] = geometryHash;
			byte[] canonicalManifest = CadCanonicalJson.Serialize(manifest);

			using (FileStream output = new(
				temporaryPackage,
				FileMode.CreateNew,
				FileAccess.ReadWrite,
				FileShare.None))
			using (ZipArchive archive = new(output, ZipArchiveMode.Create, true, Encoding.UTF8))
			{
				WriteEntry(archive, "geometry.step", geometry);
				WriteEntry(archive, "patches.json", canonicalManifest);
				output.Flush(true);
			}

			Validate(temporaryPackage, geometryHash, canonicalManifest);
			if (File.Exists(fullPath))
			{
				File.Replace(temporaryPackage, fullPath, backup, true);
				File.Delete(backup);
			}
			else
			{
				File.Move(temporaryPackage, fullPath);
			}

			return new CadGasPackageInfo(
				fullPath,
				Hash(File.ReadAllBytes(fullPath)),
				geometryHash,
				Hash(canonicalManifest));
		}
		finally
		{
			File.Delete(temporaryPackage);
			File.Delete(backup);
		}
	}

	internal static string Hash(ReadOnlySpan<byte> value)
	{
		return Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
	}

	private static void WriteEntry(ZipArchive archive, string name, byte[] content)
	{
		ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
		entry.LastWriteTime = FixedZipTime;
		entry.ExternalAttributes = 0;
		using Stream stream = entry.Open();
		stream.Write(content);
	}

	private static byte[] NormalizeStep(byte[] content)
	{
		string text = Encoding.UTF8.GetString(content);
		string normalized = Regex.Replace(
			text,
			@"FILE_NAME\s*\(\s*'[^']*'\s*,\s*'[^']*'",
			"FILE_NAME('geometry.step','1980-01-01T00:00:00'",
			RegexOptions.CultureInvariant,
			TimeSpan.FromSeconds(1));
		int occurrence = 0;
		normalized = Regex.Replace(
			normalized,
			@"NEXT_ASSEMBLY_USAGE_OCCURRENCE\s*\(\s*'[^']*'",
			_ => $"NEXT_ASSEMBLY_USAGE_OCCURRENCE('{++occurrence}'",
			RegexOptions.CultureInvariant,
			TimeSpan.FromSeconds(1));
		return Encoding.UTF8.GetBytes(normalized);
	}

	private static void Validate(
		string packagePath,
		string expectedGeometryHash,
		byte[] expectedManifest)
	{
		using ZipArchive archive = ZipFile.OpenRead(packagePath);
		if (archive.Entries.Count != 2
			|| archive.Entries[0].FullName != "geometry.step"
			|| archive.Entries[1].FullName != "patches.json")
		{
			throw new InvalidDataException("The gas package does not have the required deterministic entry layout.");
		}
		using MemoryStream geometry = new();
		using (Stream source = archive.Entries[0].Open()) source.CopyTo(geometry);
		if (!string.Equals(Hash(geometry.ToArray()), expectedGeometryHash, StringComparison.Ordinal))
		{
			throw new InvalidDataException("The gas package geometry hash does not match its exported STEP payload.");
		}
		using MemoryStream manifest = new();
		using (Stream source = archive.Entries[1].Open()) source.CopyTo(manifest);
		if (!manifest.ToArray().AsSpan().SequenceEqual(expectedManifest))
		{
			throw new InvalidDataException("The gas package manifest changed while the package was written.");
		}
	}
}
