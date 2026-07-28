using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FishGfx.Cad;

namespace FishGfx.CFD;

public sealed record GasOpeningFingerprint(
	double Area,
	double[] Centroid,
	double[] Normal,
	int LoopCount,
	double Perimeter,
	double[] EdgeLengths,
	double[][] LoopSamples,
	string SurfaceType);

public sealed record GasOpeningManifest(
	string Id,
	string PatchName,
	string Role,
	string ComponentId,
	GasOpeningFingerprint Fingerprint);

public sealed record GasPathManifest(
	string Id,
	string Kind,
	string Name,
	string ComponentName,
	GasOpeningManifest[] Openings);

public sealed record GasPackageManifest(
	string Schema,
	int Version,
	string Units,
	string GeometrySha256,
	CadPatchMatchingPolicy MatchingPolicy,
	GasPathManifest[] Paths);

public sealed record LoadedGasPackage(
	string PackagePath,
	string PackageFileHash,
	string GeometryStepHash,
	byte[] GeometryStep,
	byte[] CanonicalManifest,
	GasPackageManifest Manifest)
{
	public string ComputeSourceHash(
		string gasPathId,
		IReadOnlyDictionary<string, string>? manualOverrides = null)
	{
		if (!Manifest.Paths.Any(path => path.Id == gasPathId))
		{
			throw new InvalidDataException($"Gas path '{gasPathId}' does not exist in the package.");
		}
		JsonObject source = new()
		{
			["geometryStepSha256"] = GeometryStepHash,
			["manifestSha256"] = CfdJson.Hash(CanonicalManifest),
			["matchingPolicyVersion"] = Manifest.MatchingPolicy.Version,
			["selectedGasPathId"] = gasPathId,
		};
		JsonObject overrides = new();
		foreach ((string key, string value) in (manualOverrides
			?? new Dictionary<string, string>()).OrderBy(item => item.Key, StringComparer.Ordinal))
		{
			overrides[key] = value;
		}
		source["manualClassificationOverrides"] = overrides;
		return CfdJson.Hash(CfdJson.Serialize(source));
	}
}

public static class GasPackageReader
{
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNameCaseInsensitive = true,
	};

	public static LoadedGasPackage Load(string path)
	{
		string fullPath = Path.GetFullPath(path);
		byte[] packageBytes = File.ReadAllBytes(fullPath);
		using MemoryStream source = new(packageBytes, false);
		using ZipArchive archive = new(source, ZipArchiveMode.Read, false, Encoding.UTF8);
		if (archive.Entries.Count != 2
			|| archive.Entries[0].FullName != "geometry.step"
			|| archive.Entries[1].FullName != "patches.json")
		{
			throw new InvalidDataException("The .fggas package must contain geometry.step then patches.json.");
		}
		byte[] geometry = ReadEntry(archive.Entries[0]);
		byte[] manifestBytes = ReadEntry(archive.Entries[1]);
		JsonNode manifestNode = JsonNode.Parse(manifestBytes)
			?? throw new InvalidDataException("The gas manifest is empty.");
		byte[] canonicalManifest = CfdJson.Serialize(manifestNode);
		if (!manifestBytes.AsSpan().SequenceEqual(canonicalManifest))
		{
			throw new InvalidDataException("patches.json is not canonically serialized.");
		}
		GasPackageManifest manifest = JsonSerializer.Deserialize<GasPackageManifest>(
			manifestBytes,
			JsonOptions) ?? throw new InvalidDataException("The gas manifest could not be decoded.");
		Validate(manifest, geometry);
		return new LoadedGasPackage(
			fullPath,
			CfdJson.Hash(packageBytes),
			CfdJson.Hash(geometry),
			geometry,
			canonicalManifest,
			manifest);
	}

	private static void Validate(GasPackageManifest manifest, byte[] geometry)
	{
		if (manifest.Schema != "fishgfx.gas-patches" || manifest.Version != 1 || manifest.Units != "mm")
		{
			throw new InvalidDataException("The gas manifest schema, version, or units are unsupported.");
		}
		if (!string.Equals(manifest.GeometrySha256, CfdJson.Hash(geometry), StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidDataException("The package STEP hash does not match patches.json.");
		}
		if (manifest.MatchingPolicy != CadPatchMatchingPolicy.Version1)
		{
			throw new InvalidDataException("The package patch-matching policy is unsupported.");
		}
		if (manifest.Paths.Length == 0
			|| manifest.Paths.Any(path => path.Openings.Count(opening => opening.Role == "outlet") != 1
				|| path.Openings.All(opening => opening.Role != "inlet")))
		{
			throw new InvalidDataException("Every gas path must contain at least one inlet and exactly one outlet.");
		}
		string[] patchNames = manifest.Paths.SelectMany(path => path.Openings)
			.Select(opening => opening.PatchName).ToArray();
		if (patchNames.Distinct(StringComparer.Ordinal).Count() != patchNames.Length)
		{
			throw new InvalidDataException("Gas patch names must be unique across the package.");
		}
	}

	private static byte[] ReadEntry(ZipArchiveEntry entry)
	{
		using Stream input = entry.Open();
		using MemoryStream output = new();
		input.CopyTo(output);
		return output.ToArray();
	}
}

internal static class CfdJson
{
	internal static readonly JsonSerializerOptions Options = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		WriteIndented = true,
	};

	internal static byte[] Serialize(JsonNode node)
	{
		using MemoryStream output = new();
		using Utf8JsonWriter writer = new(output);
		WriteNode(writer, node);
		writer.Flush();
		return output.ToArray();
	}

	internal static string Hash(ReadOnlySpan<byte> bytes) =>
		Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

	private static void WriteNode(Utf8JsonWriter writer, JsonNode node)
	{
		switch (node)
		{
			case JsonObject value:
				writer.WriteStartObject();
				foreach ((string key, JsonNode? child) in value.OrderBy(item => item.Key, StringComparer.Ordinal))
				{
					writer.WritePropertyName(key);
					if (child is null) writer.WriteNullValue(); else WriteNode(writer, child);
				}
				writer.WriteEndObject();
				break;
			case JsonArray value:
				writer.WriteStartArray();
				foreach (JsonNode? child in value)
				{
					if (child is null) writer.WriteNullValue(); else WriteNode(writer, child);
				}
				writer.WriteEndArray();
				break;
			case JsonValue value:
				value.WriteTo(writer);
				break;
		}
	}
}
