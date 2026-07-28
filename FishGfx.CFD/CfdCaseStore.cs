using System.Text.Json;
using System.Text.Json.Nodes;

namespace FishGfx.CFD;

public static class CfdCaseStore
{
	public static CfdCaseDocument Load(string path)
	{
		CfdCaseDocument result = JsonSerializer.Deserialize<CfdCaseDocument>(
			File.ReadAllBytes(path),
			CfdJson.Options) ?? throw new InvalidDataException("The CFD case is empty.");
		if (result.Schema != CfdCaseDocument.SchemaName || result.Version != CfdCaseDocument.CurrentVersion)
		{
			throw new InvalidDataException("The CFD case schema or version is unsupported.");
		}
		result.Mesh.Validate();
		result.Solver.Validate();
		return result;
	}

	public static void Save(string path, CfdCaseDocument document)
	{
		document.Mesh.Validate();
		document.Solver.Validate();
		string fullPath = Path.GetFullPath(path);
		Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
		string temporary = fullPath + $".{Guid.NewGuid():N}.tmp";
		string backup = fullPath + $".{Guid.NewGuid():N}.bak";
		try
		{
			byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(document, CfdJson.Options);
			using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
			{
				output.Write(bytes);
				output.Flush(true);
			}
			if (File.Exists(fullPath))
			{
				File.Replace(temporary, fullPath, backup, true);
				File.Delete(backup);
			}
			else
			{
				File.Move(temporary, fullPath);
			}
		}
		finally
		{
			File.Delete(temporary);
			File.Delete(backup);
		}
	}

	public static string ComputeMeshHash(
		CfdCaseDocument document,
		CfdToolchainFingerprint toolchain)
	{
		JsonObject value = new()
		{
			["mesh"] = JsonSerializer.SerializeToNode(document.Mesh, CfdJson.Options),
			["sourceHash"] = document.SourceHash,
			["toolchain"] = JsonSerializer.SerializeToNode(toolchain, CfdJson.Options),
		};
		return CfdJson.Hash(CfdJson.Serialize(value));
	}

	public static string ComputeSolveHash(
		CfdCaseDocument document,
		CfdToolchainFingerprint toolchain,
		string meshHash)
	{
		JsonObject value = new()
		{
			["meshHash"] = meshHash,
			["solver"] = JsonSerializer.SerializeToNode(document.Solver, CfdJson.Options),
			["toolchain"] = JsonSerializer.SerializeToNode(toolchain, CfdJson.Options),
		};
		return CfdJson.Hash(CfdJson.Serialize(value));
	}
}
