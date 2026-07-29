using System.Text.Json;
using System.Text.Json.Nodes;

namespace FishGfx.CFD;

public static class CfdCaseStore
{
	public static CfdCaseDocument Load(string path)
	{
		JsonObject root = JsonNode.Parse(File.ReadAllBytes(path)) as JsonObject
			?? throw new InvalidDataException("The CFD case is empty.");
		string? schema = root["schema"]?.GetValue<string>();
		int version = root["version"]?.GetValue<int>() ?? 0;
		if (schema != CfdCaseDocument.SchemaName || version is < 1 or > CfdCaseDocument.CurrentVersion)
		{
			throw new InvalidDataException("The CFD case schema or version is unsupported.");
		}
		if (version == 1) root = MigrateV1(root);
		CfdCaseDocument result = root.Deserialize<CfdCaseDocument>(CfdJson.Options)
			?? throw new InvalidDataException("The CFD case is empty.");
		result.Validate();
		return result;
	}

	public static void Save(string path, CfdCaseDocument document)
	{
		document.Validate();
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
		JsonObject meshingToolchain = new()
		{
			["distribution"] = toolchain.Distribution,
			["foamVersion"] = toolchain.FoamVersion,
			["projectVersion"] = toolchain.ProjectVersion,
			["wmOptions"] = toolchain.WmOptions,
			["environmentScriptPath"] = toolchain.EnvironmentScriptPath,
			["environmentScriptSha256"] = toolchain.EnvironmentScriptSha256,
			["snappySettingsVersion"] = toolchain.SnappySettingsVersion,
			["matchingPolicyVersion"] = toolchain.MatchingPolicyVersion,
		};
		JsonObject value = new()
		{
			["mesh"] = JsonSerializer.SerializeToNode(document.Mesh, CfdJson.Options),
			["sourceHash"] = document.SourceHash,
			["toolchain"] = meshingToolchain,
		};
		return CfdJson.Hash(CfdJson.Serialize(value));
	}

	public static string ComputeSolveHash(
		CfdCaseDocument document,
		CfdToolchainFingerprint toolchain,
		string meshHash)
	{
		JsonObject solver = JsonSerializer.SerializeToNode(document.Solver, CfdJson.Options) as JsonObject
			?? throw new InvalidDataException("Solver settings could not be canonicalized.");
		// Runtime retention changes failure diagnostics, not the numerical problem.
		solver.Remove("retainFailedRuntime");
		JsonObject value = new()
		{
			["meshHash"] = meshHash,
			["analysisMode"] = document.AnalysisMode.ToString(),
			["solver"] = solver,
			["solverTemplateVersion"] = toolchain.TemplateVersion,
			["environmentScriptSha256"] = toolchain.EnvironmentScriptSha256,
		};
		if (document.AnalysisMode == CfdAnalysisMode.Steady)
			value["postProcessingVersion"] = toolchain.PostProcessingVersion;
		if (document.AnalysisMode == CfdAnalysisMode.EngineTransient)
		{
			CfdEngineTransientSettings transient = document.EngineTransient
				?? throw new InvalidDataException("Transient settings are missing.");
			CfdTransientPulseSet pulse = CfdTransientPulseGenerator.Generate(transient, document.Solver);
			value["engineTransient"] = JsonSerializer.SerializeToNode(transient, CfdJson.Options);
			value["pulseTableSha256"] = pulse.Sha256();
			value["pulseGeneratorVersion"] = CfdEngineTransientSettings.PulseGeneratorVersion;
			value["periodicityAlgorithmVersion"] = CfdEngineTransientSettings.PeriodicityAlgorithmVersion;
		}
		return CfdJson.Hash(CfdJson.Serialize(value));
	}

	public static string ComputeCaptureHash(CfdCaseDocument document, string solveHash)
	{
		if (document.AnalysisMode != CfdAnalysisMode.EngineTransient || document.EngineTransient is null)
			throw new InvalidOperationException("CaptureHash is defined only for engine-transient cases.");
		document.Capture.Validate(document.EngineTransient);
		JsonObject value = new()
		{
			["solveHash"] = solveHash,
			["capture"] = JsonSerializer.SerializeToNode(document.Capture, CfdJson.Options),
			["captureVersion"] = CfdCaptureSettings.CaptureVersion,
			["postProcessingVersion"] = OpenFoamCaseGenerator.PostProcessingVersion,
		};
		return CfdJson.Hash(CfdJson.Serialize(value));
	}

	public static string ComputeResultHash(CfdCaseDocument document, string captureHash)
	{
		document.ResultStorage.Validate();
		JsonObject value = new()
		{
			["captureHash"] = captureHash,
			["storage"] = JsonSerializer.SerializeToNode(document.ResultStorage, CfdJson.Options),
			["formatVersion"] = CfdResultStorageSettings.FormatVersion,
			["samplingPolicyVersion"] = CfdResultStorageSettings.SamplingPolicyVersion,
			["compressionVersion"] = CfdResultStorageSettings.CompressionVersion,
		};
		return CfdJson.Hash(CfdJson.Serialize(value));
	}

	private static JsonObject MigrateV1(JsonObject source)
	{
		JsonObject result = (JsonObject)source.DeepClone();
		JsonNode? steady = result["results"]?.DeepClone();
		result["version"] = CfdCaseDocument.CurrentVersion;
		result["analysisMode"] = JsonValue.Create(CfdAnalysisMode.Steady);
		result["results"] = new JsonObject { ["steady"] = steady };
		return result;
	}
}
