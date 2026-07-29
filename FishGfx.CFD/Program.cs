using FishGfx.Cad;

namespace FishGfx.CFD;

internal static class Program
{
	[STAThread]
	private static async Task<int> Main(string[] args)
	{
		try
		{
			if (args.Length == 0)
			{
				using CfdViewerApplication viewer = new((FishGfx.Cad.CadTessellation?)null);
				viewer.Run();
				return 0;
			}
			return args[0].ToLowerInvariant() switch
			{
				"inspect" when args.Length == 2 => Inspect(args[1]),
				"create" when args.Length is 3 or 4 => Create(args[1], args[2], args.Length == 4 ? args[3] : null),
				"create-transient" when args.Length >= 3 => CreateTransient(args),
				"set-quality" when args.Length == 3 => SetQuality(args[1], args[2]),
				"set-mass-flow" when args.Length == 3 => SetMassFlow(args[1], args[2]),
				"prepare" when args.Length == 2 => await Prepare(args[1]),
				"run" when args.Length == 2 => await Run(args[1]),
				"run-view" when args.Length == 2 => await RunAndView(args[1]),
				"view" when args.Length is 2 or 3 => View(args[1], args.Length == 3 ? args[2] : null),
				_ => Usage(),
			};
		}
		catch (Exception exception)
		{
			Console.Error.WriteLine(exception);
			return 1;
		}
	}

	private static int CreateTransient(string[] args)
	{
		string packagePath = args[1];
		string casePath = args[2];
		string preset = CfdEngineTransientSettings.CorsaPresetId;
		string? steadyCasePath = null;
		CfdMeshQuality quality = CfdMeshQuality.Production;
		bool qualityWasSpecified = false;
		for (int index = 3; index < args.Length; ++index)
		{
			if (args[index] == "--preset" && index + 1 < args.Length) preset = args[++index];
			else if (args[index] == "--initial-steady" && index + 1 < args.Length) steadyCasePath = args[++index];
			else if (args[index] == "--quality" && index + 1 < args.Length)
			{
				quality = CfdMeshQualityPresets.Parse(args[++index]);
				qualityWasSpecified = true;
			}
			else throw new ArgumentException($"Unknown create-transient argument '{args[index]}'.");
		}
		if (!string.Equals(preset, CfdEngineTransientSettings.CorsaPresetId, StringComparison.Ordinal))
			throw new ArgumentException($"Unsupported transient preset '{preset}'.");
		LoadedGasPackage package = GasPackageReader.Load(packagePath);
		GasPathManifest path = package.Manifest.Paths.SingleOrDefault(value => value.Kind == "collector")
			?? package.Manifest.Paths[0];
		GasOpeningManifest[] inlets = path.Openings.Where(value => value.Role == "inlet")
			.OrderByDescending(value => value.Fingerprint.Centroid[0]).ToArray();
		if (inlets.Length != 4)
			throw new InvalidDataException("The Corsa transient preset requires exactly four inlet openings.");
		string? steadyHash = null;
		Guid? steadyCaseId = null;
		CfdCaseDocument? steadyInitialization = null;
		if (steadyCasePath != null)
		{
			CfdCaseDocument steady = CfdCaseStore.Load(steadyCasePath);
			if (steady.AnalysisMode != CfdAnalysisMode.Steady
				|| steady.Results.Steady == null
				|| steady.Results.Steady.Status != CfdRunStatus.Converged
				|| string.IsNullOrWhiteSpace(steady.SolveHash))
				throw new InvalidDataException("The initialization case is not a converged compatible steady case.");
			if (steady.PackageFileHash != package.PackageFileHash
				|| steady.SourceHash != package.ComputeSourceHash(path.Id)
				|| steady.SelectedGasPathId != path.Id
				|| steady.Solver.TotalMassFlowKgPerSecond != CfdSolverSettings.CorsaEstimatedMassFlowKgPerSecond
				|| steady.Solver.InletTemperatureK != 900
				|| steady.Solver.OutletPressurePa != 101325)
				throw new InvalidDataException("The steady initialization case is incompatible with the Corsa source and operating point.");
			steadyHash = steady.SolveHash;
			steadyCaseId = steady.CaseId;
			steadyInitialization = steady;
		}
		CfdEngineTransientSettings transient = new()
		{
			CylinderAssignments = inlets.Select((opening, index) =>
				new CfdCylinderAssignment(index + 1, opening.ComponentId)).ToList(),
			InitialisationMode = steadyHash == null
				? TransientInitialisationMode.Uniform
				: TransientInitialisationMode.CompatibleSteadyResult,
			InitialSteadySolveHash = steadyHash,
			InitialSteadyCaseId = steadyCaseId,
		};
		transient = CfdMeshQualityPresets.CorsaTransient(transient, quality);
		transient.ValidateAgainst(path);
		CfdMeshSettings requestedMesh = CfdMeshQualityPresets.Corsa(quality);
		if (steadyInitialization != null && qualityWasSpecified && steadyInitialization.Mesh != requestedMesh)
		{
			transient = transient with
			{
				InitialisationMode = TransientInitialisationMode.MappedSteadyPreview,
			};
		}
		string fullCase = Path.GetFullPath(casePath);
		string relative = Path.GetRelativePath(Path.GetDirectoryName(fullCase)!, package.PackagePath);
		CfdSolverSettings solver = CfdMeshQualityPresets.CorsaSolver(
			steadyInitialization?.Solver ?? new CfdSolverSettings());
		CfdCaseDocument document = new()
		{
			SourcePackagePath = relative,
			PackageFileHash = package.PackageFileHash,
			SelectedGasPathId = path.Id,
			SourceHash = package.ComputeSourceHash(path.Id),
			AnalysisMode = CfdAnalysisMode.EngineTransient,
			EngineTransient = transient,
			Mesh = steadyInitialization != null && !qualityWasSpecified
				? steadyInitialization.Mesh
				: requestedMesh,
			Solver = solver,
			Capture = CfdMeshQualityPresets.CorsaCapture(new CfdCaptureSettings(), quality),
		};
		CfdCaseStore.Save(fullCase, document);
		Console.WriteLine($"Created {fullCase}");
		return 0;
	}

	private static int SetQuality(string casePath, string qualityName)
	{
		string fullCase = Path.GetFullPath(casePath);
		CfdCaseDocument document = CfdCaseStore.Load(fullCase);
		if (document.AnalysisMode != CfdAnalysisMode.EngineTransient)
			throw new InvalidDataException("Mesh quality presets currently apply to engine-transient cases.");
		CfdMeshQuality quality = CfdMeshQualityPresets.Parse(qualityName);
		CfdMeshSettings mesh = CfdMeshQualityPresets.Corsa(quality);
		CfdEngineTransientSettings transient = document.EngineTransient!;
		bool hasSteadyInitialization = transient.InitialisationMode is
			TransientInitialisationMode.CompatibleSteadyResult or
			TransientInitialisationMode.MappedSteadyPreview;
		bool mappedSteady = hasSteadyInitialization && mesh != document.Mesh;
		if (mappedSteady)
		{
			transient = transient with
			{
				InitialisationMode = quality == CfdMeshQuality.Production
					? TransientInitialisationMode.CompatibleSteadyResult
					: TransientInitialisationMode.MappedSteadyPreview,
			};
		}
		transient = CfdMeshQualityPresets.CorsaTransient(transient, quality);
		CfdCaptureSettings capture = CfdMeshQualityPresets.CorsaCapture(document.Capture, quality);
		document = document with
		{
			Mesh = mesh,
			EngineTransient = transient,
			Capture = capture,
			Toolchain = null,
			MeshHash = null,
			SolveHash = null,
			CaptureHash = null,
			ResultHash = null,
			Results = new CfdCaseResults(),
		};
		CfdCaseStore.Save(fullCase, document);
		Console.WriteLine($"Set {fullCase} to the {CfdMeshQualityPresets.Name(quality)} quality preset.");
		if (quality == CfdMeshQuality.Preview)
		{
			Console.WriteLine(
				"Preview uses a two-cycle limit, 4-degree output, coarse meshing, and stabilized low-cost solver controls.");
		}
		if (mappedSteady && quality != CfdMeshQuality.Production)
		{
			Console.WriteLine(
				"The converged production steady field will be mapped onto the lower-quality mesh before transient startup.");
		}
		return 0;
	}

	private static int SetMassFlow(string casePath, string value)
	{
		if (!double.TryParse(
			value,
			System.Globalization.NumberStyles.Float,
			System.Globalization.CultureInfo.InvariantCulture,
			out double massFlow)
			|| !double.IsFinite(massFlow)
			|| massFlow <= 0)
		{
			throw new ArgumentException("Mass flow must be a finite positive value in kg/s.");
		}
		string fullCase = Path.GetFullPath(casePath);
		CfdCaseDocument document = CfdCaseStore.Load(fullCase);
		document = document with
		{
			Solver = document.Solver with { TotalMassFlowKgPerSecond = massFlow },
			SolveHash = null,
			CaptureHash = null,
			ResultHash = null,
			Results = new CfdCaseResults(),
		};
		CfdCaseStore.Save(fullCase, document);
		Console.WriteLine(FormattableString.Invariant(
			$"Set {fullCase} aggregate mass flow to {massFlow:R} kg/s."));
		return 0;
	}

	private static int Inspect(string packagePath)
	{
		LoadedGasPackage package = GasPackageReader.Load(packagePath);
		Console.WriteLine($"PackageFileHash: {package.PackageFileHash}");
		Console.WriteLine($"GeometryStepHash: {package.GeometryStepHash}");
		foreach (GasPathManifest path in package.Manifest.Paths)
		{
			Console.WriteLine($"{path.Kind} {path.Id} '{path.Name}': {path.Openings.Count(item => item.Role == "inlet")} inlet(s), 1 outlet");
		}
		return 0;
	}

	private static int Create(string packagePath, string casePath, string? selectedPath)
	{
		LoadedGasPackage package = GasPackageReader.Load(packagePath);
		GasPathManifest path = selectedPath is null
			? package.Manifest.Paths[0]
			: package.Manifest.Paths.Single(item => item.Id == selectedPath);
		double diameter = path.Openings.Where(item => item.Role == "inlet")
			.Min(item => 2 * Math.Sqrt(item.Fingerprint.Area / Math.PI));
		string relative = Path.GetRelativePath(Path.GetDirectoryName(Path.GetFullPath(casePath))!, package.PackagePath);
		CfdCaseDocument document = new()
		{
			SourcePackagePath = relative,
			PackageFileHash = package.PackageFileHash,
			SelectedGasPathId = path.Id,
			SourceHash = package.ComputeSourceHash(path.Id),
			Mesh = new CfdMeshSettings
			{
				FirstLayerThicknessMm = CfdMeshSettings.DefaultFirstLayerThickness(diameter),
			},
		};
		CfdCaseStore.Save(casePath, document);
		Console.WriteLine($"Created {Path.GetFullPath(casePath)}");
		return 0;
	}

	private static async Task<int> Prepare(string casePath)
	{
		(CfdCaseDocument document, LoadedGasPackage package, PreparedCfdPackage prepared, string work) =
			await PrepareCore(casePath);
		Console.WriteLine($"Prepared {work}");
		Console.WriteLine($"MeshHash: {document.MeshHash}");
		Console.WriteLine($"SolveHash: {document.SolveHash}");
		Console.WriteLine($"Matched openings: {prepared.Diagnostics.Count}");
		return 0;
	}

	private static async Task<int> Run(string casePath)
	{
		CfdCaseDocument previous = CfdCaseStore.Load(Path.GetFullPath(casePath));
		(CfdCaseDocument document, LoadedGasPackage package, _, string work) = await PrepareCore(casePath);
		if (document.AnalysisMode == CfdAnalysisMode.EngineTransient)
			return await RunTransient(casePath, previous, document, package, work);
		if (previous.Results.Steady != null
			&& string.Equals(previous.MeshHash, document.MeshHash, StringComparison.Ordinal)
			&& string.Equals(previous.SolveHash, document.SolveHash, StringComparison.Ordinal))
		{
			GasPathManifest cachedPath = package.Manifest.Paths.Single(
				item => item.Id == document.SelectedGasPathId);
			VerifiedOpenFoamResults verified = OpenFoamResultVerifier.Verify(
				Path.Combine(work, "results"),
				cachedPath);
			CfdResultSummary cachedMetrics = CfdMetrics.Calculate(
				verified.Boundaries,
				document.Solver.Fluid,
				previous.Results.Steady.Status);
			CfdResultSummary enriched = previous.Results.Steady with
			{
				Iterations = Math.Max(previous.Results.Steady.Iterations, ReadIterationCount(Path.Combine(work, "results", "run.log"))),
				DensityConsistencyMaximumRelativeError = cachedMetrics.DensityConsistencyMaximumRelativeError,
				Residuals = previous.Results.Steady.Residuals.Count > 0
					? previous.Results.Steady.Residuals
					: ReadResiduals(Path.Combine(work, "results", "run.log")),
			};
			document = document with { Results = document.Results with { Steady = enriched } };
			CfdCaseStore.Save(casePath, document);
			Console.WriteLine($"CacheHit: {document.SolveHash}");
			return enriched.Status == CfdRunStatus.Converged ? 0 : 2;
		}
		WslOpenFoamEnvironment environment = await WslOpenFoamEnvironment.DetectAsync(document.AnalysisMode);
		WslOpenFoamRunner runner = new(environment);
		OpenFoamRunResult result = await runner.RunAsync(
			work,
			document,
			document.CaseId,
			document.MeshHash!,
			document.SolveHash!,
			document.Solver.RetainFailedRuntime);
		CfdResultSummary summary = new() { Status = result.Status, Diagnostic = result.Diagnostic };
		if (result.Status is CfdRunStatus.Converged or CfdRunStatus.MaximumIterations)
		{
			GasPathManifest path = package.Manifest.Paths.Single(item => item.Id == document.SelectedGasPathId);
			VerifiedOpenFoamResults verified = OpenFoamResultVerifier.Verify(result.WindowsResultDirectory, path);
			CfdResultSummary metrics = CfdMetrics.Calculate(
				verified.Boundaries,
				document.Solver.Fluid,
				result.Status);
			string? statusDiagnostic = result.Status == CfdRunStatus.MaximumIterations
				? "Maximum iterations reached without residualControl convergence."
				: null;
			summary = metrics with
			{
				Iterations = ReadIterationCount(result.LogPath),
				Residuals = ReadResiduals(result.LogPath),
				Diagnostic = string.Join(
					" ",
					new[] { statusDiagnostic, metrics.Diagnostic }
						.Where(value => !string.IsNullOrWhiteSpace(value))),
			};
		}
		document = document with
		{
			Results = document.Results with { Steady = summary },
		};
		CfdCaseStore.Save(casePath, document);
		Console.WriteLine($"{result.Status}: {result.LogPath}");
		return result.Status == CfdRunStatus.Converged ? 0 : 2;
	}

	private static async Task<int> RunAndView(string casePath)
	{
		int status = await Run(casePath);
		CfdCaseDocument document = CfdCaseStore.Load(Path.GetFullPath(casePath));
		if (document.Results.Steady == null && document.Results.Transient == null) return status;
		return ViewCase(casePath);
	}

	private static async Task<int> RunTransient(
		string casePath,
		CfdCaseDocument previous,
		CfdCaseDocument document,
		LoadedGasPackage package,
		string work)
	{
		string fullCase = Path.GetFullPath(casePath);
		if (previous.Results.Transient is CfdTransientResultReference cached
			&& previous.SolveHash == document.SolveHash
			&& previous.CaptureHash == document.CaptureHash
			&& previous.ResultHash == document.ResultHash)
		{
			string cachedPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(fullCase)!, cached.RelativePath));
			if (File.Exists(cachedPath))
			{
				using FgFlowResultSequence validation = new(cachedPath, cached.Sha256);
				if (validation.FrameCount == cached.FrameCount)
				{
					Console.WriteLine($"CacheHit: {document.ResultHash}");
					return cached.Periodicity.Passed ? 0 : 2;
				}
			}
		}

		string retainedResults = Path.Combine(work, "results");
		bool reuseCapture = previous.Results.Transient != null
			&& previous.SolveHash == document.SolveHash
			&& Directory.Exists(Path.Combine(retainedResults, "VTK"))
			&& Directory.Exists(Path.Combine(retainedResults, "postProcessing"));
		OpenFoamRunResult run;
		if (reuseCapture)
		{
			run = new(
				previous.Results.Transient!.Periodicity.Passed
					? CfdRunStatus.PeriodicConverged
					: CfdRunStatus.MaximumCyclesWithoutPeriodicity,
				retainedResults,
				Path.Combine(retainedResults, "run.log"),
				"Re-ingesting retained transient capture data without solving.",
				previous.Results.Transient.AcceptedCycle);
		}
		else
		{
			WslOpenFoamEnvironment environment = await WslOpenFoamEnvironment.DetectAsync(document.AnalysisMode);
			WslOpenFoamRunner runner = new(environment);
			run = await runner.RunAsync(
				work,
				document,
				document.CaseId,
				document.MeshHash!,
				document.SolveHash!,
				document.Solver.RetainFailedRuntime);
		}
		if (run.Status is CfdRunStatus.FatalError or CfdRunStatus.Cancelled or CfdRunStatus.TimeStepCollapse)
		{
			document = document with
			{
				Results = document.Results with
				{
					TransientSummary = new CfdTransientResultSummary
					{
						Status = run.Status,
						Diagnostic = run.Diagnostic,
					},
				},
			};
			CfdCaseStore.Save(fullCase, document);
			return 2;
		}

		CfdEngineTransientSettings transient = document.EngineTransient!;
		int acceptedCycle = run.AcceptedCycle ?? transient.MaximumCycles;
		GasPathManifest path = package.Manifest.Paths.Single(value => value.Id == document.SelectedGasPathId);
		double captureStart = (acceptedCycle - 1) * transient.CycleDurationSeconds;
		int frameCount = checked((int)Math.Round(720.0 / document.Capture.RetainedOutputAngleDegrees));
		IEnumerable<CfdFlowFrameSource> frames = OpenFoamResultVerifier.VerifyTransientFrames(
			run.WindowsResultDirectory,
			path,
			captureStart,
			transient.CycleDurationSeconds,
			document.Capture.RetainedOutputAngleDegrees);
		string resultDirectory = fullCase + ".results";
		string resultPath = Path.Combine(resultDirectory, "transient.fgflow");
		List<CfdTransientFrameMetric> frameMetrics = [];
		List<CfdTransientFluxSample> fluxSamples = [];
		string resultFileHash = FgFlowWriter.WriteStreaming(
			resultPath,
			document.SolveHash!,
			document.CaptureHash!,
			acceptedCycle,
			frameCount,
			CollectTransientMetrics(frames, document, path, frameMetrics, fluxSamples),
			document.ResultStorage);
		CfdPeriodicityResult periodicity = OpenFoamTransientMonitor.ReadAndCompareCycle(
			run.WindowsResultDirectory,
			transient,
			acceptedCycle);
		CfdRunStatus resultStatus = periodicity.Passed
			? CfdRunStatus.PeriodicConverged
			: CfdRunStatus.MaximumCyclesWithoutPeriodicity;
		IReadOnlyList<CfdTransientFluxSample> closedFluxSamples = CloseCycle(
			fluxSamples,
			transient.CycleDurationSeconds);
		double? cyclePressureLoss = CfdTransientMetricCalculator.CycleAveragePressureLoss(
			closedFluxSamples,
			1e-5);
		double inletMass = IntegrateMass(closedFluxSamples, value => value.InletMassFlowKgPerSecond);
		double outletMass = IntegrateMass(closedFluxSamples, value => value.OutletMassFlowKgPerSecond);
		double massImbalance = Math.Abs(inletMass - outletMass)
			/ Math.Max(Math.Max(Math.Abs(inletMass), Math.Abs(outletMass)), double.Epsilon);
		string relativeResult = Path.GetRelativePath(Path.GetDirectoryName(fullCase)!, resultPath);
		document = document with
		{
			Results = document.Results with
			{
				Transient = new(relativeResult, resultFileHash, acceptedCycle, frameCount, periodicity),
				TransientSummary = new CfdTransientResultSummary
				{
					Status = resultStatus,
					CycleAveragePressureLossPa = cyclePressureLoss,
					CycleMassImbalanceFraction = massImbalance,
					Frames = frameMetrics,
					Diagnostic = periodicity.Passed
						? $"Cycle {acceptedCycle} satisfies every periodicity criterion."
						: $"Cycle {acceptedCycle} remains viewable but did not satisfy every periodicity criterion.",
				},
			},
		};
		CfdCaseStore.Save(fullCase, document);
		Console.WriteLine($"{document.Results.TransientSummary!.Status}: {resultPath}");
		return periodicity.Passed ? 0 : 2;
	}

	private static IEnumerable<CfdFlowFrameSource> CollectTransientMetrics(
		IEnumerable<CfdFlowFrameSource> frames,
		CfdCaseDocument document,
		GasPathManifest path,
		List<CfdTransientFrameMetric> metrics,
		List<CfdTransientFluxSample> fluxes)
	{
		foreach (CfdFlowFrameSource frame in frames)
		{
			HashSet<string> closedInlets = NominallyClosedInlets(
				path,
				document.EngineTransient!,
				frame.CrankAngleDegrees);
			(CfdTransientFrameMetric metric, CfdTransientFluxSample flux) = CfdMetrics.CalculateTransientFrame(
				frame.Index,
				frame.TimeSeconds,
				frame.CrankAngleDegrees,
				frame.Results.Boundaries,
				document.Solver.Fluid,
				document.Capture.MinimumMetricMassFlowKgPerSecond,
				closedInlets);
			metrics.Add(metric);
			fluxes.Add(flux);
			yield return frame;
		}
	}

	private static HashSet<string> NominallyClosedInlets(
		GasPathManifest path,
		CfdEngineTransientSettings transient,
		double crankAngleDegrees)
	{
		Dictionary<int, double> phases = transient.FiringOrder
			.Select((cylinder, index) => (cylinder, phase: index * 720.0 / transient.FiringOrder.Length))
			.ToDictionary(value => value.cylinder, value => value.phase);
		Dictionary<string, int> cylinders = transient.CylinderAssignments
			.ToDictionary(value => value.ComponentId, value => value.CylinderNumber, StringComparer.Ordinal);
		return path.Openings.Where(value => value.Role == "inlet").Where(opening =>
		{
			double local = (crankAngleDegrees - phases[cylinders[opening.ComponentId]] + 720.0) % 720.0;
			return local < transient.EventStartDegreesAfterFiring
				|| local > transient.EventEndDegreesAfterFiring;
		}).Select(value => value.PatchName).ToHashSet(StringComparer.Ordinal);
	}

	private static double IntegrateMass(
		IReadOnlyList<CfdTransientFluxSample> samples,
		Func<CfdTransientFluxSample, double> selector)
	{
		double result = 0;
		for (int index = 1; index < samples.Count; ++index)
		{
			double dt = samples[index].TimeSeconds - samples[index - 1].TimeSeconds;
			result += 0.5 * (selector(samples[index - 1]) + selector(samples[index])) * dt;
		}
		return result;
	}

	private static IReadOnlyList<CfdTransientFluxSample> CloseCycle(
		IReadOnlyList<CfdTransientFluxSample> samples,
		double cycleDurationSeconds)
	{
		if (samples.Count == 0) return samples;
		CfdTransientFluxSample first = samples[0];
		return samples.Append(first with { TimeSeconds = first.TimeSeconds + cycleDurationSeconds }).ToArray();
	}

	private static int ReadIterationCount(string logPath)
	{
		int result = 0;
		foreach (string line in File.ReadLines(logPath))
		{
			string value = line.Trim();
			if (!value.StartsWith("Time = ", StringComparison.Ordinal)
				|| !value.EndsWith('s'))
			{
				continue;
			}
			string number = value[7..^1];
			if (double.TryParse(
				number,
				System.Globalization.NumberStyles.Float,
				System.Globalization.CultureInfo.InvariantCulture,
				out double time))
			{
				result = Math.Max(result, checked((int)Math.Round(time)));
			}
		}
		return result;
	}

	private static IReadOnlyList<CfdResidualSample> ReadResiduals(string logPath)
	{
		System.Text.RegularExpressions.Regex expression = new(
			@"Solving for ([^,]+), Initial residual = ([^,]+), Final residual = ([^,]+), No Iterations (\d+)",
			System.Text.RegularExpressions.RegexOptions.CultureInvariant);
		List<CfdResidualSample> result = [];
		int iteration = 0;
		foreach (string line in File.ReadLines(logPath))
		{
			string value = line.Trim();
			if (value.StartsWith("Time = ", StringComparison.Ordinal) && value.EndsWith('s')
				&& double.TryParse(
					value[7..^1],
					System.Globalization.NumberStyles.Float,
					System.Globalization.CultureInfo.InvariantCulture,
					out double time))
			{
				iteration = checked((int)Math.Round(time));
				continue;
			}
			System.Text.RegularExpressions.Match match = expression.Match(value);
			if (!match.Success) continue;
			result.Add(new CfdResidualSample(
				iteration,
				match.Groups[1].Value,
				double.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture),
				double.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture),
				int.Parse(match.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture)));
		}
		return result;
	}

	private static int View(string packagePath, string? pathId)
	{
		if (string.Equals(Path.GetExtension(packagePath), ".fgcfd", StringComparison.OrdinalIgnoreCase))
		{
			return ViewCase(packagePath);
		}
		LoadedGasPackage package = GasPackageReader.Load(packagePath);
		pathId ??= package.Manifest.Paths[0].Id;
		string temporary = Path.Combine(Path.GetTempPath(), $"fishgfx-cfd-view-{Guid.NewGuid():N}");
		try
		{
			PreparedCfdPackage prepared = CfdGeometryPipeline.Prepare(package, pathId, temporary);
			using CfdViewerApplication viewer = new(prepared.Tessellation);
			viewer.Run();
			return 0;
		}
		finally
		{
			if (Directory.Exists(temporary)) Directory.Delete(temporary, true);
		}
	}

	private static int ViewCase(string casePath)
	{
		string fullCase = Path.GetFullPath(casePath);
		CfdCaseDocument document = CfdCaseStore.Load(fullCase);
		if (document.Results.Steady == null && document.Results.Transient == null)
			return ViewUnsolvedCase(fullCase, document);
		if (document.AnalysisMode == CfdAnalysisMode.EngineTransient)
			return ViewTransientCase(fullCase, document);
		string packagePath = Path.GetFullPath(Path.Combine(
			Path.GetDirectoryName(fullCase)!,
			document.SourcePackagePath));
		LoadedGasPackage package = GasPackageReader.Load(packagePath);
		GasPathManifest path = package.Manifest.Paths.Single(item => item.Id == document.SelectedGasPathId);
		if (document.AnalysisMode == CfdAnalysisMode.EngineTransient)
			document.EngineTransient!.ValidateAgainst(path);
		VerifiedOpenFoamResults results = OpenFoamResultVerifier.Verify(fullCase + ".work\\results", path);
		using CfdViewerApplication viewer = new(null, results, document.Results.Steady);
		viewer.Run();
		return 0;
	}

	private static int ViewUnsolvedCase(string fullCasePath, CfdCaseDocument document)
	{
		string packagePath = Path.GetFullPath(Path.Combine(
			Path.GetDirectoryName(fullCasePath)!,
			document.SourcePackagePath));
		LoadedGasPackage package = GasPackageReader.Load(packagePath);
		string temporary = Path.Combine(Path.GetTempPath(), $"fishgfx-cfd-case-view-{Guid.NewGuid():N}");
		try
		{
			PreparedCfdPackage prepared = CfdGeometryPipeline.Prepare(
				package,
				document.SelectedGasPathId,
				temporary);
			Console.WriteLine(
				"No transient result exists yet. Showing the published gas geometry; use the explicit 'run' command to solve.");
			using CfdViewerApplication viewer = new(prepared.Tessellation);
			viewer.Run();
			return 0;
		}
		finally
		{
			if (Directory.Exists(temporary)) Directory.Delete(temporary, true);
		}
	}

	private static int ViewTransientCase(string fullCasePath, CfdCaseDocument document)
	{
		CfdTransientResultReference reference = document.Results.Transient
			?? throw new InvalidOperationException("The transient case has no persisted FGFLOW result.");
		string resultPath = Path.GetFullPath(Path.Combine(
			Path.GetDirectoryName(fullCasePath)!,
			reference.RelativePath));
		using FgFlowResultSequence sequence = new(resultPath, reference.Sha256);
		if (sequence.FrameCount != reference.FrameCount)
			throw new InvalidDataException("The FGFLOW frame count does not match the case reference.");
		using CfdViewerApplication viewer = new(
			sequence,
			transient: document.EngineTransient,
			transientResult: reference,
			transientSummary: document.Results.TransientSummary);
		viewer.Run();
		return 0;
	}

	private static async Task<(CfdCaseDocument, LoadedGasPackage, PreparedCfdPackage, string)> PrepareCore(
		string casePath)
	{
		string fullCase = Path.GetFullPath(casePath);
		CfdCaseDocument document = CfdCaseStore.Load(fullCase);
		string packagePath = Path.GetFullPath(Path.Combine(
			Path.GetDirectoryName(fullCase)!,
			document.SourcePackagePath));
		LoadedGasPackage package = GasPackageReader.Load(packagePath);
		if (package.PackageFileHash != document.PackageFileHash
			|| package.ComputeSourceHash(document.SelectedGasPathId, document.ManualClassificationOverrides) != document.SourceHash)
		{
			throw new InvalidDataException("The CFD source package or classifications changed; recreate or update the case.");
		}
		string work = fullCase + ".work";
		PreparedCfdPackage prepared = CfdGeometryPipeline.Prepare(
			package,
			document.SelectedGasPathId,
			work);
		WslOpenFoamEnvironment environment = await WslOpenFoamEnvironment.DetectAsync(document.AnalysisMode);
		string meshHash = CfdCaseStore.ComputeMeshHash(document, environment.Fingerprint);
		string solveHash = CfdCaseStore.ComputeSolveHash(document, environment.Fingerprint, meshHash);
		string? captureHash = document.AnalysisMode == CfdAnalysisMode.EngineTransient
			? CfdCaseStore.ComputeCaptureHash(document, solveHash)
			: null;
		string? resultHash = captureHash == null ? null : CfdCaseStore.ComputeResultHash(document, captureHash);
		document = document with
		{
			Toolchain = environment.Fingerprint,
			MeshHash = meshHash,
			SolveHash = solveHash,
			CaptureHash = captureHash,
			ResultHash = resultHash,
			MatchingDiagnostics = prepared.Diagnostics.ToList(),
		};
		OpenFoamCaseGenerator.Generate(work, document, package, prepared.Geometry);
		CfdCaseStore.Save(fullCase, document);
		return (document, package, prepared, work);
	}

	private static int Usage()
	{
		Console.Error.WriteLine("FishGfx.CFD inspect <gas.fggas>");
		Console.Error.WriteLine("FishGfx.CFD create <gas.fggas> <case.fgcfd> [path-id]");
		Console.Error.WriteLine("FishGfx.CFD create-transient <gas.fggas> <case.fgcfd> --preset corsa-3500 [--quality preview|balanced|production] [--initial-steady <case.fgcfd>]");
		Console.Error.WriteLine("FishGfx.CFD set-quality <case.fgcfd> preview|balanced|production");
		Console.Error.WriteLine("FishGfx.CFD set-mass-flow <case.fgcfd> <kg/s>");
		Console.Error.WriteLine("FishGfx.CFD prepare <case.fgcfd>");
		Console.Error.WriteLine("FishGfx.CFD run <case.fgcfd>");
		Console.Error.WriteLine("FishGfx.CFD run-view <case.fgcfd>");
		Console.Error.WriteLine("FishGfx.CFD view <gas.fggas> [path-id]");
		Console.Error.WriteLine("FishGfx.CFD view <case.fgcfd>");
		return 64;
	}
}
