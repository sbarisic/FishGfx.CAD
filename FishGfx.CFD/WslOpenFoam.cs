using System.Diagnostics;
using System.Text;

namespace FishGfx.CFD;

public sealed record WslOpenFoamEnvironment(
	string Distribution,
	string EnvironmentScript,
	CfdToolchainFingerprint Fingerprint)
{
	public static async Task<WslOpenFoamEnvironment> DetectAsync(
		string distribution = "Ubuntu",
		string environmentScript = "/opt/openfoam14/etc/bashrc",
		CancellationToken cancellationToken = default)
	{
		string command = $"test -f {Q(environmentScript)}; source {Q(environmentScript)} >/dev/null 2>&1; "
			+ "for c in foamRun surfaceCheck blockMesh surfaceFeatures snappyHexMesh checkMesh foamPostProcess foamToVTK; do command -v \"$c\" >/dev/null || { echo missing-command:$c >&2; exit 41; }; done; "
			+ "printf '%s-%s\\n%s\\n%s\\n' \"${WM_PROJECT:-OpenFOAM}\" \"${WM_PROJECT_VERSION:-unknown}\" \"${WM_PROJECT_VERSION:-}\" \"${WM_OPTIONS:-}\"; "
			+ $"sha256sum {Q(environmentScript)} | awk '{{print $1}}'";
		ProcessResult result = await RunProcessAsync(
			"wsl.exe",
			["-d", distribution, "--", "bash", "-lc", EncodeBash(command)],
			cancellationToken);
		if (result.ExitCode != 0)
		{
			throw new InvalidOperationException(
				$"Foundation OpenFOAM 14 is unavailable in WSL '{distribution}': {result.StandardError}");
		}
		string[] lines = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		if (lines.Length < 4)
		{
			throw new InvalidDataException("OpenFOAM environment discovery returned incomplete metadata.");
		}
		CfdToolchainFingerprint fingerprint = new(
			"Foundation",
			lines[0],
			lines[1],
			lines[2],
			environmentScript,
			lines[3],
			OpenFoamCaseGenerator.TemplateVersion,
			CfdMeshSettings.SettingsVersion,
			FishGfx.Cad.CadPatchMatchingPolicy.Version1.Version,
			OpenFoamCaseGenerator.PostProcessingVersion);
		return new WslOpenFoamEnvironment(distribution, environmentScript, fingerprint);
	}

	internal static string Q(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

	internal static string EncodeBash(string script)
	{
		string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(script));
		return $"echo {encoded} | base64 -d | bash";
	}

	internal static async Task<ProcessResult> RunProcessAsync(
		string fileName,
		IEnumerable<string> arguments,
		CancellationToken cancellationToken)
	{
		using Process process = new();
		process.StartInfo = new ProcessStartInfo(fileName)
		{
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true,
		};
		foreach (string argument in arguments) process.StartInfo.ArgumentList.Add(argument);
		if (!process.Start()) throw new InvalidOperationException($"Could not start {fileName}.");
		Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
		Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
		await process.WaitForExitAsync(cancellationToken);
		return new ProcessResult(process.ExitCode, await output, await error);
	}
}

internal readonly record struct ProcessResult(int ExitCode, string StandardOutput, string StandardError);

public sealed record OpenFoamRunResult(
	CfdRunStatus Status,
	string WindowsResultDirectory,
	string LogPath,
	string Diagnostic);

public sealed class WslOpenFoamRunner
{
	private readonly WslOpenFoamEnvironment environment;

	public WslOpenFoamRunner(WslOpenFoamEnvironment environment)
	{
		this.environment = environment;
	}

	public async Task<OpenFoamRunResult> RunAsync(
		string windowsCaseDirectory,
		Guid caseId,
		string meshHash,
		string solveHash,
		bool retainFailedRuntime,
		CancellationToken cancellationToken = default)
	{
		string fullCase = Path.GetFullPath(windowsCaseDirectory);
		string results = Path.Combine(fullCase, "results");
		Directory.CreateDirectory(results);
		WriteScripts(fullCase, environment.EnvironmentScript, meshHash);
		string windowsWslPath = await GetWslPath(fullCase, cancellationToken);
		string runtime = $"$HOME/.local/share/FishGfx.CFD/cases/{caseId:D}/{solveHash}";
		string prepare = $"set -e; runtime={runtime}; rm -rf \"$runtime\"; mkdir -p \"$runtime\"; cp -a {WslOpenFoamEnvironment.Q(windowsWslPath + "/.")} \"$runtime/\"; cd \"$runtime\"; bash run-wrapper.sh";
		Task<ProcessResult> run = WslOpenFoamEnvironment.RunProcessAsync(
			"wsl.exe",
			["-d", environment.Distribution, "--", "bash", "-lc", WslOpenFoamEnvironment.EncodeBash(prepare)],
			CancellationToken.None);
		using CancellationTokenRegistration registration = cancellationToken.Register(() =>
		{
			try
			{
				string cancel = $"runtime={runtime}; if test -f \"$runtime/run.pid\"; then pid=$(cat \"$runtime/run.pid\"); kill -TERM -- -$pid 2>/dev/null || true; sleep 5; kill -KILL -- -$pid 2>/dev/null || true; fi";
				WslOpenFoamEnvironment.RunProcessAsync(
					"wsl.exe",
					["-d", environment.Distribution, "--", "bash", "-lc", WslOpenFoamEnvironment.EncodeBash(cancel)],
					CancellationToken.None).GetAwaiter().GetResult();
			}
			catch
			{
				// The original run reports cancellation even if the cleanup process has already exited.
			}
		});
		ProcessResult processResult = await run;
		bool cancelled = cancellationToken.IsCancellationRequested;
		string copy = $"runtime={runtime}; mkdir -p {WslOpenFoamEnvironment.Q(windowsWslPath + "/results")}; "
			+ $"test ! -f \"$runtime/run.log\" || cp \"$runtime/run.log\" {WslOpenFoamEnvironment.Q(windowsWslPath + "/results/run.log")}; "
			+ $"test ! -f \"$runtime/run-status.txt\" || cp \"$runtime/run-status.txt\" {WslOpenFoamEnvironment.Q(windowsWslPath + "/results/run-status.txt")}; "
			+ $"test ! -d \"$runtime/VTK\" || {{ rm -rf {WslOpenFoamEnvironment.Q(windowsWslPath + "/results/VTK")}; cp -a \"$runtime/VTK\" {WslOpenFoamEnvironment.Q(windowsWslPath + "/results/VTK")}; }}; "
			+ $"test ! -d \"$runtime/mesh-cache/{meshHash}\" || {{ mkdir -p {WslOpenFoamEnvironment.Q(windowsWslPath + "/mesh-cache")}; rm -rf {WslOpenFoamEnvironment.Q(windowsWslPath + "/mesh-cache/" + meshHash)}; cp -a \"$runtime/mesh-cache/{meshHash}\" {WslOpenFoamEnvironment.Q(windowsWslPath + "/mesh-cache/" + meshHash)}; }}";
		await WslOpenFoamEnvironment.RunProcessAsync(
			"wsl.exe",
			["-d", environment.Distribution, "--", "bash", "-lc", WslOpenFoamEnvironment.EncodeBash(copy)],
			CancellationToken.None);
		string statusText = File.Exists(Path.Combine(results, "run-status.txt"))
			? File.ReadAllText(Path.Combine(results, "run-status.txt")).Trim()
			: string.Empty;
		CfdRunStatus status = cancelled ? CfdRunStatus.Cancelled : statusText switch
		{
			"converged" => CfdRunStatus.Converged,
			"maximum-iterations" => CfdRunStatus.MaximumIterations,
			"cancelled" => CfdRunStatus.Cancelled,
			_ => CfdRunStatus.FatalError,
		};
		if (status != CfdRunStatus.Converged && !retainFailedRuntime)
		{
			string remove = $"runtime={runtime}; rm -rf \"$runtime\"";
			await WslOpenFoamEnvironment.RunProcessAsync(
				"wsl.exe",
				["-d", environment.Distribution, "--", "bash", "-lc", WslOpenFoamEnvironment.EncodeBash(remove)],
				CancellationToken.None);
		}
		return new OpenFoamRunResult(
			status,
			results,
			Path.Combine(results, "run.log"),
			processResult.ExitCode == 0 ? statusText : processResult.StandardError.Trim());
	}

	private async Task<string> GetWslPath(string windowsPath, CancellationToken cancellationToken)
	{
		ProcessResult result = await WslOpenFoamEnvironment.RunProcessAsync(
			"wsl.exe",
			[
				"-d",
				environment.Distribution,
				"--",
				"bash",
				"-lc",
				WslOpenFoamEnvironment.EncodeBash($"wslpath -a {WslOpenFoamEnvironment.Q(windowsPath)}"),
			],
			cancellationToken);
		if (result.ExitCode != 0) throw new InvalidOperationException(result.StandardError);
		return result.StandardOutput.Trim();
	}

	private static void WriteScripts(string caseDirectory, string environmentScript, string meshHash)
	{
		string inner = """
			#!/usr/bin/env bash
			set -eo pipefail
			source {{ENV}} >/dev/null 2>&1
			set -u
			phase() { local name="$1"; shift; echo "FGCFD_PHASE_BEGIN:$name"; "$@"; echo "FGCFD_PHASE_END:$name"; }
			mesh_hash={{MESH_HASH}}
			if test -d "mesh-cache/$mesh_hash/polyMesh"; then
			  echo FGCFD_MESH_CACHE_HIT:$mesh_hash
			  rm -rf constant/polyMesh
			  cp -a "mesh-cache/$mesh_hash/polyMesh" constant/polyMesh
			else
			  phase surfaceCheck surfaceCheck constant/triSurface/gas-domain.stl
			  phase blockMesh blockMesh
			  phase surfaceFeatures surfaceFeatures
			  phase snappyHexMesh snappyHexMesh -overwrite
			fi
			echo FGCFD_PHASE_BEGIN:checkMesh
			checkMesh 2>&1 | tee checkMesh.log
			grep -q 'Mesh OK.' checkMesh.log
			echo FGCFD_PHASE_END:checkMesh
			if test ! -d "mesh-cache/$mesh_hash/polyMesh"; then
			  mkdir -p "mesh-cache/$mesh_hash"
			  cp -a constant/polyMesh "mesh-cache/$mesh_hash/polyMesh"
			fi
			echo FGCFD_PHASE_BEGIN:foamRun
			set +e
			foamRun -solver fluid 2>&1 | tee solver.log
			solver_status=${PIPESTATUS[0]}
			set -e
			if test "$solver_status" -ne 0 || grep -Eq 'FOAM FATAL|Floating point exception|nan|inf' solver.log; then
			  echo fatal-error > run-status.txt
			  exit 20
			fi
			echo FGCFD_PHASE_END:foamRun
			latest=$(find . -maxdepth 1 -type d -printf '%f\n' | awk '/^[0-9]+([.][0-9]+)?$/' | sort -g | tail -n 1)
			test -n "$latest"
			foamPostProcess -solver fluid -latestTime -func MachNo
			test -f "$latest/Ma" && ! grep -Eqi 'nan|inf' "$latest/Ma"
			foamPostProcess -solver fluid -latestTime -func yPlus
			test -f "$latest/yPlus" && ! grep -Eqi 'nan|inf' "$latest/yPlus"
			test -f "$latest/rho" && ! grep -Eqi 'nan|inf' "$latest/rho"
			foamToVTK -ascii -polyhedra none -latestTime -fields '(p T U rho Ma yPlus)'
			test -d VTK && find VTK -type f | grep -q .
			if grep -Eqi 'solution converged|converged in' solver.log; then
			  echo converged > run-status.txt
			else
			  echo maximum-iterations > run-status.txt
			fi
			"""
			.Replace("{{ENV}}", WslOpenFoamEnvironment.Q(environmentScript), StringComparison.Ordinal)
			.Replace("{{MESH_HASH}}", meshHash, StringComparison.Ordinal);
		string wrapper = """
			#!/usr/bin/env bash
			set -uo pipefail
			child=""
			cleanup() { if test -n "$child"; then kill -TERM -- "-$child" 2>/dev/null || true; fi; echo cancelled > run-status.txt; }
			trap cleanup TERM INT
			setsid bash run-inner.sh > run.log 2>&1 &
			child=$!
			echo "$child" > run.pid
			wait "$child"
			status=$?
			trap - TERM INT
			exit "$status"
			""";
		File.WriteAllText(Path.Combine(caseDirectory, "run-inner.sh"), inner.Replace("\r\n", "\n"), new UTF8Encoding(false));
		File.WriteAllText(Path.Combine(caseDirectory, "run-wrapper.sh"), wrapper.Replace("\r\n", "\n"), new UTF8Encoding(false));
	}
}
