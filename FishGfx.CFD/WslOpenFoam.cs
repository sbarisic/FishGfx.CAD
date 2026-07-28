using System.Diagnostics;
using System.Text;

namespace FishGfx.CFD;

public sealed record WslOpenFoamEnvironment(
	string Distribution,
	string EnvironmentScript,
	CfdToolchainFingerprint Fingerprint)
{
	public static async Task<WslOpenFoamEnvironment> DetectAsync(
		CfdAnalysisMode analysisMode = CfdAnalysisMode.Steady,
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
			OpenFoamCaseGenerator.TemplateVersionFor(analysisMode),
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
	string Diagnostic,
	int? AcceptedCycle = null);

public sealed class WslOpenFoamRunner
{
	private readonly WslOpenFoamEnvironment environment;

	public WslOpenFoamRunner(WslOpenFoamEnvironment environment)
	{
		this.environment = environment;
	}

	public async Task<OpenFoamRunResult> RunAsync(
		string windowsCaseDirectory,
		CfdCaseDocument document,
		Guid caseId,
		string meshHash,
		string solveHash,
		bool retainFailedRuntime,
		CancellationToken cancellationToken = default)
	{
		string fullCase = Path.GetFullPath(windowsCaseDirectory);
		string results = Path.Combine(fullCase, "results");
		Directory.CreateDirectory(results);
		WriteScripts(fullCase, environment.EnvironmentScript, meshHash, document);
		string windowsWslPath = await GetWslPath(fullCase, cancellationToken);
		string runtime = $"$HOME/.local/share/FishGfx.CFD/cases/{caseId:D}/{solveHash}";
		string initialize = CompatibleSteadyInitialization(
			document,
			runtime,
			meshHash,
			environment.EnvironmentScript);
		string prepare = $"set -e; runtime={runtime}; rm -rf \"$runtime\"; mkdir -p \"$runtime\"; cp -a {WslOpenFoamEnvironment.Q(windowsWslPath + "/.")} \"$runtime/\"; {initialize} cd \"$runtime\"; bash run-wrapper.sh";
		Console.WriteLine($"OpenFOAM {document.AnalysisMode} run started.");
		Console.WriteLine($"Runtime: {runtime}");
		Console.WriteLine($"Live log: {Path.Combine(results, "run.log")}");
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
		int? acceptedCycle = document.AnalysisMode == CfdAnalysisMode.EngineTransient
			? await MonitorTransientCycles(
				run,
				runtime,
				windowsWslPath,
				results,
				document.EngineTransient!,
				cancellationToken)
			: null;
		ProcessResult processResult = await run;
		bool cancelled = cancellationToken.IsCancellationRequested;
		string copy = $"runtime={runtime}; mkdir -p {WslOpenFoamEnvironment.Q(windowsWslPath + "/results")}; "
			+ $"test ! -f \"$runtime/run.log\" || cp \"$runtime/run.log\" {WslOpenFoamEnvironment.Q(windowsWslPath + "/results/run.log")}; "
			+ $"test ! -f \"$runtime/run-status.txt\" || cp \"$runtime/run-status.txt\" {WslOpenFoamEnvironment.Q(windowsWslPath + "/results/run-status.txt")}; "
			+ $"test ! -f \"$runtime/accepted-cycle.txt\" || cp \"$runtime/accepted-cycle.txt\" {WslOpenFoamEnvironment.Q(windowsWslPath + "/results/accepted-cycle.txt")}; "
			+ $"test ! -d \"$runtime/VTK\" || {{ rm -rf {WslOpenFoamEnvironment.Q(windowsWslPath + "/results/VTK")}; cp -a \"$runtime/VTK\" {WslOpenFoamEnvironment.Q(windowsWslPath + "/results/VTK")}; }}; "
			+ $"test ! -d \"$runtime/postProcessing\" || {{ rm -rf {WslOpenFoamEnvironment.Q(windowsWslPath + "/results/postProcessing")}; cp -a \"$runtime/postProcessing\" {WslOpenFoamEnvironment.Q(windowsWslPath + "/results/postProcessing")}; }}; "
			+ $"test ! -d \"$runtime/mesh-cache/{meshHash}\" || {{ mkdir -p {WslOpenFoamEnvironment.Q(windowsWslPath + "/mesh-cache")}; rm -rf {WslOpenFoamEnvironment.Q(windowsWslPath + "/mesh-cache/" + meshHash)}; cp -a \"$runtime/mesh-cache/{meshHash}\" {WslOpenFoamEnvironment.Q(windowsWslPath + "/mesh-cache/" + meshHash)}; }}";
		await WslOpenFoamEnvironment.RunProcessAsync(
			"wsl.exe",
			["-d", environment.Distribution, "--", "bash", "-lc", WslOpenFoamEnvironment.EncodeBash(copy)],
			CancellationToken.None);
		string statusText = File.Exists(Path.Combine(results, "run-status.txt"))
			? File.ReadAllText(Path.Combine(results, "run-status.txt")).Trim()
			: string.Empty;
		string acceptedCyclePath = Path.Combine(results, "accepted-cycle.txt");
		if (File.Exists(acceptedCyclePath)
			&& int.TryParse(File.ReadAllText(acceptedCyclePath).Trim(), out int capturedCycle))
			acceptedCycle = capturedCycle;
		CfdRunStatus status = cancelled ? CfdRunStatus.Cancelled : statusText switch
		{
			"converged" => CfdRunStatus.Converged,
			"maximum-iterations" => CfdRunStatus.MaximumIterations,
			"transient-complete" when acceptedCycle.HasValue => CfdRunStatus.PeriodicConverged,
			"transient-complete" => CfdRunStatus.MaximumCyclesWithoutPeriodicity,
			"timestep-collapse" => CfdRunStatus.TimeStepCollapse,
			"cancelled" => CfdRunStatus.Cancelled,
			_ => CfdRunStatus.FatalError,
		};
		if ((status is CfdRunStatus.FatalError or CfdRunStatus.Cancelled or CfdRunStatus.TimeStepCollapse)
			&& !retainFailedRuntime)
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
			processResult.ExitCode == 0 ? statusText : processResult.StandardError.Trim(),
			acceptedCycle);
	}

	private static string CompatibleSteadyInitialization(
		CfdCaseDocument document,
		string runtime,
		string meshHash,
		string environmentScript)
	{
		CfdEngineTransientSettings? transient = document.EngineTransient;
		if (document.AnalysisMode != CfdAnalysisMode.EngineTransient
			|| transient?.InitialisationMode != TransientInitialisationMode.CompatibleSteadyResult)
			return string.Empty;
		string steadyRuntime = $"$HOME/.local/share/FishGfx.CFD/cases/{transient.InitialSteadyCaseId:D}/{transient.InitialSteadySolveHash}";
		return $"source {WslOpenFoamEnvironment.Q(environmentScript)} >/dev/null 2>&1 || true; command -v foamDictionary >/dev/null || {{ echo invalid-openfoam-environment >&2; exit 43; }}; steady_runtime={steadyRuntime}; test -d \"$steady_runtime/constant/polyMesh\" || {{ echo missing-compatible-steady-checkpoint >&2; exit 42; }}; "
			+ "steady_time=$(find \"$steady_runtime\" -maxdepth 1 -type d -printf '%f\\n' | awk '/^[0-9]+([.][0-9]+)?$/' | sort -g | tail -n 1); test -n \"$steady_time\"; "
			+ $"mkdir -p \"{runtime}/mesh-cache/{meshHash}\"; cp -a \"$steady_runtime/constant/polyMesh\" \"{runtime}/mesh-cache/{meshHash}/polyMesh\"; "
			+ $"for field in U p T k omega nut alphat; do cp \"{runtime}/0/$field\" \"{runtime}/0/$field.transient\"; awk '/^boundaryField/{{exit}} {{print}}' \"$steady_runtime/$steady_time/$field\" > \"{runtime}/0/$field.merged\"; sed -n '/^boundaryField/,$p' \"{runtime}/0/$field.transient\" >> \"{runtime}/0/$field.merged\"; mv \"{runtime}/0/$field.merged\" \"{runtime}/0/$field\"; rm \"{runtime}/0/$field.transient\"; done; ";
	}

	private async Task<int?> MonitorTransientCycles(
		Task<ProcessResult> run,
		string runtime,
		string windowsWslPath,
		string windowsResults,
		CfdEngineTransientSettings settings,
		CancellationToken cancellationToken)
	{
		string monitorResults = Path.Combine(windowsResults, ".monitor");
		if (Directory.Exists(monitorResults)) Directory.Delete(monitorResults, true);
		int lastCompared = settings.MinimumCycles - 1;
		int collapsedPolls = 0;
		int progressPolls = 0;
		while (!run.IsCompleted && !cancellationToken.IsCancellationRequested)
		{
			await Task.Delay(1000, CancellationToken.None);
			string latestCommand = $"runtime={runtime}; test -d \"$runtime\" || exit 0; "
				+ "output_time=$(find \"$runtime\" -maxdepth 1 -type d -printf '%f\\n' | awk '/^[0-9]+([.][0-9]+)?$/' | sort -g | tail -n 1); "
				+ "simulation_time=$(awk '/^Time = .*s$/ { value=$3; sub(/s$/, \"\", value) } END { print value }' \"$runtime/run.log\" 2>/dev/null); "
				+ "delta_t=$(awk '/^deltaT = / { value=$3 } END { print value }' \"$runtime/run.log\" 2>/dev/null); "
				+ "printf '%s\\n%s\\n%s\\n' \"$output_time\" \"${simulation_time:-$output_time}\" \"$delta_t\"";
			ProcessResult latestResult = await WslOpenFoamEnvironment.RunProcessAsync(
				"wsl.exe",
				["-d", environment.Distribution, "--", "bash", "-lc", WslOpenFoamEnvironment.EncodeBash(latestCommand)],
				CancellationToken.None);
			string[] progress = latestResult.StandardOutput.Replace("\r", string.Empty, StringComparison.Ordinal)
				.Split('\n');
			if (progress.Length == 0
				|| !double.TryParse(progress[0], System.Globalization.NumberStyles.Float,
					System.Globalization.CultureInfo.InvariantCulture, out double latestTime)) continue;
			double simulationTime = progress.Length > 1
				&& double.TryParse(progress[1], System.Globalization.NumberStyles.Float,
					System.Globalization.CultureInfo.InvariantCulture, out double parsedSimulationTime)
					? parsedSimulationTime : latestTime;
			double? latestDeltaT = progress.Length > 2
				&& double.TryParse(progress[2], System.Globalization.NumberStyles.Float,
					System.Globalization.CultureInfo.InvariantCulture, out double parsedDeltaT)
					? parsedDeltaT : null;
			if (++progressPolls % 10 == 1)
			{
				double totalDegrees = simulationTime / settings.SecondsPerDegree;
				int cycle = Math.Min(settings.MaximumCycles, (int)(totalDegrees / 720.0) + 1);
				double crank = totalDegrees % 720.0;
				Console.WriteLine(
					$"OpenFOAM cycle {cycle}/{settings.MaximumCycles}, crank {crank:F3} deg, "
						+ $"time {simulationTime:G8} s, deltaT {latestDeltaT?.ToString("G6", System.Globalization.CultureInfo.InvariantCulture) ?? "pending"} s");
			}
			double minimumDeltaT = settings.MinimumTimeStepDegrees * settings.SecondsPerDegree;
			collapsedPolls = latestDeltaT.HasValue && latestDeltaT.Value < minimumDeltaT
				? collapsedPolls + 1 : 0;
			if (collapsedPolls >= settings.CollapsedTimeStepPollLimit)
			{
				string stop = $"runtime={runtime}; echo timestep-collapse > \"$runtime/run-status.txt\"; "
					+ "if test -f \"$runtime/run.pid\"; then pid=$(cat \"$runtime/run.pid\"); kill -TERM -- -$pid 2>/dev/null || true; fi";
				await WslOpenFoamEnvironment.RunProcessAsync(
					"wsl.exe",
					["-d", environment.Distribution, "--", "bash", "-lc", WslOpenFoamEnvironment.EncodeBash(stop)],
					CancellationToken.None);
				Console.Error.WriteLine(
					$"OpenFOAM stopped because deltaT remained below {minimumDeltaT:G6} s "
						+ $"for {settings.CollapsedTimeStepPollLimit} checks.");
				return null;
			}
			int availableCycle = Math.Min(settings.MaximumCycles,
				(int)Math.Floor((latestTime + settings.MaximumTimeStepDegrees * settings.SecondsPerDegree * 0.5)
					/ settings.CycleDurationSeconds));
			if (availableCycle < settings.MinimumCycles || availableCycle <= lastCompared) continue;
			string monitorWslPath = windowsWslPath + "/results/.monitor";
			string copy = $"runtime={runtime}; rm -rf {WslOpenFoamEnvironment.Q(monitorWslPath)}; "
				+ $"mkdir -p {WslOpenFoamEnvironment.Q(monitorWslPath)}; cp -a \"$runtime/postProcessing\" {WslOpenFoamEnvironment.Q(monitorWslPath + "/postProcessing")}";
			await WslOpenFoamEnvironment.RunProcessAsync(
				"wsl.exe",
				["-d", environment.Distribution, "--", "bash", "-lc", WslOpenFoamEnvironment.EncodeBash(copy)],
				CancellationToken.None);
			try
			{
				CfdPeriodicityResult periodicity = OpenFoamTransientMonitor.ReadAndCompareCycle(
					monitorResults,
					settings,
					availableCycle);
				lastCompared = availableCycle;
				if (!periodicity.Passed) continue;
				string stop = $"source {WslOpenFoamEnvironment.Q(environment.EnvironmentScript)} >/dev/null 2>&1; runtime={runtime}; "
					+ $"echo {availableCycle} > \"$runtime/accepted-cycle.txt\"; foamDictionary \"$runtime/system/controlDict\" -entry stopAt -set writeNow";
				await WslOpenFoamEnvironment.RunProcessAsync(
					"wsl.exe",
					["-d", environment.Distribution, "--", "bash", "-lc", WslOpenFoamEnvironment.EncodeBash(stop)],
					CancellationToken.None);
				return availableCycle;
			}
			catch (InvalidDataException)
			{
				// A function-object file may have been copied while OpenFOAM was appending to it.
			}
		}
		return null;
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

	private static void WriteScripts(
		string caseDirectory,
		string environmentScript,
		string meshHash,
		CfdCaseDocument document)
	{
		string solveAndPostProcess = document.AnalysisMode == CfdAnalysisMode.EngineTransient
			? TransientSolveScript(document)
			: SteadySolveScript();
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
			{{SOLVE_AND_POST}}
			"""
			.Replace("{{ENV}}", WslOpenFoamEnvironment.Q(environmentScript), StringComparison.Ordinal)
			.Replace("{{MESH_HASH}}", meshHash, StringComparison.Ordinal)
			.Replace("{{SOLVE_AND_POST}}", solveAndPostProcess, StringComparison.Ordinal);
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

	private static string SteadySolveScript() => """
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
		""";

	private static string TransientSolveScript(CfdCaseDocument document)
	{
		CfdEngineTransientSettings settings = document.EngineTransient!;
		string cycleDuration = settings.CycleDurationSeconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
		double frameDurationSeconds = document.Capture.RetainedOutputAngleDegrees * settings.SecondsPerDegree;
		string frameDuration = frameDurationSeconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
		int frameCount = checked((int)Math.Round(720.0 / document.Capture.RetainedOutputAngleDegrees));
		int groupCount = checked((frameCount + 4) / 5);
		string maximumCycles = settings.MaximumCycles.ToString(System.Globalization.CultureInfo.InvariantCulture);
		return """
			echo FGCFD_PHASE_BEGIN:foamRun-transient
			set +e
			foamRun -solver fluid 2>&1 | tee solver.log
			solver_status=${PIPESTATUS[0]}
			set -e
			if test "$solver_status" -ne 0 || grep -Eq 'FOAM FATAL|Floating point exception|nan|inf' solver.log; then
			  echo fatal-error > run-status.txt
			  exit 20
			fi
			echo FGCFD_PHASE_END:foamRun-transient
			if test ! -f accepted-cycle.txt; then echo {{MAX_CYCLES}} > accepted-cycle.txt; fi
			accepted_cycle=$(cat accepted-cycle.txt)
			capture_start=$(awk -v c="$accepted_cycle" -v d={{CYCLE_DURATION}} 'BEGIN { printf "%.17g", (c-1)*d }')
			capture_end=$(awk -v c="$accepted_cycle" -v d={{CYCLE_DURATION}} 'BEGIN { printf "%.17g", c*d }')
			capture_range="$capture_start:$capture_end"
			foamPostProcess -solver fluid -time "$capture_range" -func MachNo
			foamPostProcess -solver fluid -time "$capture_range" -func yPlus
			find . -maxdepth 2 -type f \( -name Ma -o -name yPlus -o -name rho \) -print0 | xargs -0 grep -Eqi 'nan|inf' && exit 22 || true
			# Convert the retained half-open cycle in deterministic groups of five configured frames.
			# The restart-only 720-degree state is excluded.
			for group in $(seq 0 {{LAST_GROUP}}); do
			  group_start=$(awk -v s="$capture_start" -v g="$group" -v f={{FRAME_DURATION}} 'BEGIN { printf "%.17g", s + g*5*f }')
			  group_last=$(awk -v g="$group" -v n={{FRAME_COUNT}} 'BEGIN { i=g*5+4; if (i >= n) i=n-1; print i-g*5 }')
			  group_end=$(awk -v s="$group_start" -v f={{FRAME_DURATION}} -v n="$group_last" 'BEGIN { printf "%.17g", s + n*f }')
			  foamToVTK -ascii -polyhedra none -time "$group_start:$group_end" -fields '(p T U rho Ma yPlus)'
			  test -d VTK && find VTK -type f | grep -q .
			done
			echo transient-complete > run-status.txt
			"""
			.Replace("{{MAX_CYCLES}}", maximumCycles, StringComparison.Ordinal)
			.Replace("{{CYCLE_DURATION}}", cycleDuration, StringComparison.Ordinal)
			.Replace("{{FRAME_DURATION}}", frameDuration, StringComparison.Ordinal)
			.Replace("{{FRAME_COUNT}}", frameCount.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
			.Replace("{{LAST_GROUP}}", (groupCount - 1).ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
	}
}
