using System.Text;
using System.Text.Json;

namespace FishGfx.CFD;

public sealed record CfdGpuToolchainManifest
{
	public const string SchemaName = "fishgfx.cfd-gpu-toolchain";
	public const int CurrentVersion = 1;
	public string Schema { get; init; } = SchemaName;
	public int Version { get; init; } = CurrentVersion;
	public string Distribution { get; init; } = "Foundation";
	public string OpenFoamVersion { get; init; } = "14";
	public string WmOptions { get; init; } = string.Empty;
	public string OpenFoamEnvironmentScriptPath { get; init; } = string.Empty;
	public string OpenFoamEnvironmentScriptSha256 { get; init; } = string.Empty;
	public string RocmVersion { get; init; } = string.Empty;
	public string HipVersion { get; init; } = string.Empty;
	public string GpuName { get; init; } = string.Empty;
	public string GpuPciAddress { get; init; } = string.Empty;
	public IReadOnlyList<string> GpuArchitectures { get; init; } = [];
	public string PetscGitCommit { get; init; } = string.Empty;
	public string PetscConfigurationSha256 { get; init; } = string.Empty;
	public string PetscScalarType { get; init; } = string.Empty;
	public string PetscPrecision { get; init; } = string.Empty;
	public int PetscIndexBits { get; init; }
	public string HypreVersion { get; init; } = string.Empty;
	public string HypreConfiguration { get; init; } = string.Empty;
	public string AdapterGitCommit { get; init; } = string.Empty;
	public string AdapterPortVersion { get; init; } = string.Empty;
	public string AdapterAbi { get; init; } = string.Empty;
	public string AdapterLibraryPath { get; init; } = string.Empty;
	public string AdapterSha256 { get; init; } = string.Empty;

	public void Validate(string projectVersion, string wmOptions)
	{
		if (Schema != SchemaName || Version != CurrentVersion
			|| Distribution != "Foundation"
			|| OpenFoamVersion != projectVersion
			|| WmOptions != wmOptions
			|| string.IsNullOrWhiteSpace(OpenFoamEnvironmentScriptPath)
			|| !Sha256(OpenFoamEnvironmentScriptSha256)
			|| string.IsNullOrWhiteSpace(RocmVersion)
			|| string.IsNullOrWhiteSpace(HipVersion)
			|| string.IsNullOrWhiteSpace(GpuName)
			|| string.IsNullOrWhiteSpace(GpuPciAddress)
			|| GpuArchitectures.Count == 0
			|| !Commit(PetscGitCommit)
			|| !Sha256(PetscConfigurationSha256)
			|| PetscScalarType != "real"
			|| PetscPrecision != "double"
			|| PetscIndexBits != 32
			|| string.IsNullOrWhiteSpace(HypreVersion)
			|| !HypreConfiguration.Contains("HIP", StringComparison.OrdinalIgnoreCase)
			|| !Commit(AdapterGitCommit)
			|| string.IsNullOrWhiteSpace(AdapterPortVersion)
			|| AdapterAbi != $"foundation-openfoam{projectVersion}-{wmOptions}-v1"
			|| string.IsNullOrWhiteSpace(AdapterLibraryPath)
			|| !Sha256(AdapterSha256))
		{
			throw new InvalidDataException("The external AMD GPU toolchain manifest is incompatible or incomplete.");
		}
	}

	private static bool Sha256(string value) =>
		value.Length == 64 && value.All(Uri.IsHexDigit);
	private static bool Commit(string value) =>
		value.Length >= 12 && value.All(Uri.IsHexDigit);
}

public sealed record CfdGpuSmokeResult
{
	public const string SchemaName = "fishgfx.cfd-gpu-smoke";
	public string Schema { get; init; } = string.Empty;
	public bool AdapterLoaded { get; init; }
	public bool PetscHipActive { get; init; }
	public bool HypreHipActive { get; init; }
	public int DeviceIndex { get; init; }
	public string DeviceName { get; init; } = string.Empty;
	public string DevicePciAddress { get; init; } = string.Empty;
	public string DeviceArchitecture { get; init; } = string.Empty;
	public int Iterations { get; init; }
	public double InitialResidual { get; init; }
	public double FinalResidual { get; init; }

	public void Validate(CfdGpuToolchainManifest manifest, int deviceIndex)
	{
		if (Schema != SchemaName || !AdapterLoaded || !PetscHipActive || !HypreHipActive
			|| DeviceIndex != deviceIndex || DeviceName != manifest.GpuName
			|| DevicePciAddress != manifest.GpuPciAddress
			|| !manifest.GpuArchitectures.Contains(DeviceArchitecture, StringComparer.Ordinal)
			|| Iterations < 1 || !double.IsFinite(InitialResidual) || InitialResidual <= 0
			|| !double.IsFinite(FinalResidual) || FinalResidual < 0
			|| FinalResidual >= InitialResidual)
		{
			throw new InvalidDataException("The AMD GPU PETSc/Hypre smoke test did not prove valid device execution.");
		}
	}
}

public static class CfdGpuToolchainDetector
{
	public const string EnvironmentVariable = "FISHGFX_CFD_GPU_ENV";
	public const string SmokeCommand = "fishgfx-cfd-petsc-smoke";

	public static async Task<WslOpenFoamEnvironment> DetectAsync(
		CfdAnalysisMode analysisMode,
		CfdComputeSettings compute,
		string distribution = "Ubuntu",
		CancellationToken cancellationToken = default)
	{
		compute.Validate();
		if (compute.Backend != CfdComputeBackend.AmdGpuPetsc)
			throw new ArgumentException("The GPU detector requires the AMD GPU compute backend.", nameof(compute));
		string environmentScript = Environment.GetEnvironmentVariable(EnvironmentVariable)
			?? throw new InvalidOperationException(
				$"AMD GPU execution requires {EnvironmentVariable} to name the external WSL activation script.");
		string script = DiscoveryScript(environmentScript, compute.DeviceIndex);
		ProcessResult result = await WslOpenFoamEnvironment.RunProcessAsync(
			"wsl.exe",
			["-d", distribution, "--", "bash", "-lc", WslOpenFoamEnvironment.EncodeBash(script)],
			cancellationToken);
		if (result.ExitCode != 0)
			throw new InvalidOperationException("AMD GPU toolchain validation failed: " + result.StandardError.Trim());
		string[] lines = result.StandardOutput.Replace("\r", string.Empty, StringComparison.Ordinal)
			.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		if (lines.Length < 8)
			throw new InvalidDataException("AMD GPU toolchain discovery returned incomplete metadata.");

		byte[] manifestBytes = Convert.FromBase64String(lines[5]);
		CfdGpuToolchainManifest manifest = JsonSerializer.Deserialize<CfdGpuToolchainManifest>(manifestBytes, CfdJson.Options)
			?? throw new InvalidDataException("The AMD GPU toolchain manifest is empty.");
		manifest.Validate(lines[1], lines[2]);
		string manifestHash = CfdJson.Hash(manifestBytes);
		if (!string.Equals(lines[7], manifest.AdapterSha256, StringComparison.OrdinalIgnoreCase))
			throw new InvalidDataException("The Foundation OpenFOAM adapter hash does not match its manifest.");
		CfdGpuSmokeResult smoke = JsonSerializer.Deserialize<CfdGpuSmokeResult>(
			Convert.FromBase64String(lines[6]), CfdJson.Options)
			?? throw new InvalidDataException("The AMD GPU smoke-test result is empty.");
		smoke.Validate(manifest, compute.DeviceIndex);

		CfdToolchainFingerprint fingerprint = new(
			"Foundation",
			lines[0],
			lines[1],
			lines[2],
			manifest.OpenFoamEnvironmentScriptPath,
			manifest.OpenFoamEnvironmentScriptSha256,
			OpenFoamCaseGenerator.TemplateVersionFor(analysisMode),
			CfdMeshSettings.SettingsVersion,
			FishGfx.Cad.CadPatchMatchingPolicy.Version1.Version,
			OpenFoamCaseGenerator.PostProcessingVersion,
			compute.Backend,
			compute.SolverProfile,
			smoke.DeviceName,
			manifest.GpuPciAddress,
			compute.DeviceIndex,
			smoke.DeviceArchitecture,
			manifest.RocmVersion,
			manifest.HipVersion,
			manifest.PetscGitCommit,
			manifest.PetscConfigurationSha256,
			manifest.HypreVersion,
			manifest.HypreConfiguration,
			manifest.AdapterGitCommit,
			manifest.AdapterPortVersion,
			manifest.AdapterAbi,
			manifest.AdapterSha256,
			manifestHash,
			environmentScript,
			lines[3]);
		return new WslOpenFoamEnvironment(distribution, environmentScript, fingerprint);
	}

	private static string DiscoveryScript(string environmentScript, int deviceIndex) =>
		$"set -euo pipefail; test -f {WslOpenFoamEnvironment.Q(environmentScript)}; "
		+ $"source {WslOpenFoamEnvironment.Q(environmentScript)} >/dev/null 2>&1; "
		+ "for c in foamRun surfaceCheck blockMesh surfaceFeatures snappyHexMesh checkMesh foamPostProcess foamToVTK python3 sha256sum "
		+ $"{SmokeCommand}; do command -v \"$c\" >/dev/null || {{ echo missing-command:$c >&2; exit 41; }}; done; "
		+ "manifest=${FISHGFX_CFD_GPU_MANIFEST:-}; test -n \"$manifest\" && test -f \"$manifest\" || { echo missing-gpu-manifest >&2; exit 42; }; "
		+ "adapter=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))[\"adapterLibraryPath\"])' \"$manifest\"); test -f \"$adapter\" || { echo missing-petsc-adapter >&2; exit 43; }; "
		+ "foam_env=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))[\"openFoamEnvironmentScriptPath\"])' \"$manifest\"); foam_env_hash=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))[\"openFoamEnvironmentScriptSha256\"])' \"$manifest\"); test -f \"$foam_env\" && test \"$(sha256sum \"$foam_env\" | awk '{print $1}')\" = \"$foam_env_hash\" || { echo openfoam-environment-hash-mismatch >&2; exit 44; }; "
		+ "adapter_probe=$(foamRun -case /tmp/fishgfx-cfd-gpu-doctor-noncase -libs \"(\\\"$adapter\\\")\" -solver fluid 2>&1 || true); echo \"$adapter_probe\" | grep -Eq 'could not load|dlopen error' && { echo openfoam-adapter-load-failed >&2; exit 45; }; "
		+ $"smoke=$({SmokeCommand} --device {deviceIndex} --json); "
		+ "printf '%s-%s\\n%s\\n%s\\n' \"${WM_PROJECT:-OpenFOAM}\" \"${WM_PROJECT_VERSION:-unknown}\" \"${WM_PROJECT_VERSION:-}\" \"${WM_OPTIONS:-}\"; "
		+ $"sha256sum {WslOpenFoamEnvironment.Q(environmentScript)} | awk '{{print $1}}'; "
		+ "printf '%s\\n' \"$manifest\"; base64 -w0 \"$manifest\"; printf '\\n'; printf '%s' \"$smoke\" | base64 -w0; printf '\\n'; sha256sum \"$adapter\" | awk '{print $1}'";
}
