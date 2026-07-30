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
		if (version == 1)
		{
			root = MigrateV1(root);
			version = 2;
		}
		if (version == 2)
		{
			root = MigrateV2(root);
			version = 3;
		}
		if (version == 3) root = MigrateV3(root);
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
			["compute"] = JsonSerializer.SerializeToNode(document.Compute, CfdJson.Options),
			["computeToolchain"] = ComputeToolchain(toolchain),
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
			value["operatingPoint"] = JsonSerializer.SerializeToNode(document.OperatingPoint, CfdJson.Options);
			value["turbineBoundary"] = JsonSerializer.SerializeToNode(document.TurbineBoundary, CfdJson.Options);
			if (document.TurbineBoundary.Mode == CfdOutletBoundaryMode.TurbineMapImpedance)
			{
				value["turbineMapPreset"] = JsonSerializer.SerializeToNode(
					CfdTurbineMaps.Resolve(document.TurbineBoundary.PresetId),
					CfdJson.Options);
			}
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
		result["version"] = 2;
		result["analysisMode"] = JsonValue.Create(CfdAnalysisMode.Steady);
		result["results"] = new JsonObject { ["steady"] = steady };
		return result;
	}

	private static JsonObject MigrateV2(JsonObject source)
	{
		JsonObject result = (JsonObject)source.DeepClone();
		result["version"] = 3;
		result["operatingPoint"] = null;
		result["turbineBoundary"] = JsonSerializer.SerializeToNode(
			new CfdTurbineBoundarySettings
			{
				Mode = CfdOutletBoundaryMode.WaveTransmissiveFarField,
			},
			CfdJson.Options);
		return result;
	}

	private static JsonObject MigrateV3(JsonObject source)
	{
		JsonObject result = (JsonObject)source.DeepClone();
		result["version"] = CfdCaseDocument.CurrentVersion;
		result["compute"] = JsonSerializer.SerializeToNode(
			CfdComputeSettings.For(CfdComputeBackend.AmdGpuPetsc),
			CfdJson.Options);
		return result;
	}

	private static JsonObject ComputeToolchain(CfdToolchainFingerprint toolchain) => new()
	{
		["backend"] = toolchain.ComputeBackend.ToString(),
		["solverProfile"] = toolchain.SolverProfile,
		["gpuName"] = toolchain.GpuName,
		["gpuPciAddress"] = toolchain.GpuPciAddress,
		["gpuDeviceIndex"] = toolchain.GpuDeviceIndex,
		["gpuArchitecture"] = toolchain.GpuArchitecture,
		["rocmVersion"] = toolchain.RocmVersion,
		["hipVersion"] = toolchain.HipVersion,
		["petscGitCommit"] = toolchain.PetscGitCommit,
		["petscConfigurationSha256"] = toolchain.PetscConfigurationSha256,
		["hypreVersion"] = toolchain.HypreVersion,
		["hypreConfiguration"] = toolchain.HypreConfiguration,
		["adapterGitCommit"] = toolchain.AdapterGitCommit,
		["adapterPortVersion"] = toolchain.AdapterPortVersion,
		["adapterAbi"] = toolchain.AdapterAbi,
		["adapterSha256"] = toolchain.AdapterSha256,
		["gpuManifestSha256"] = toolchain.GpuManifestSha256,
		["computeEnvironmentScriptPath"] = toolchain.ComputeEnvironmentScriptPath,
		["computeEnvironmentScriptSha256"] = toolchain.ComputeEnvironmentScriptSha256,
	};
}
