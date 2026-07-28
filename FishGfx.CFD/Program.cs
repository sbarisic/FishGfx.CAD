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
				using CfdViewerApplication viewer = new(null);
				viewer.Run();
				return 0;
			}
			return args[0].ToLowerInvariant() switch
			{
				"inspect" when args.Length == 2 => Inspect(args[1]),
				"create" when args.Length is 3 or 4 => Create(args[1], args[2], args.Length == 4 ? args[3] : null),
				"prepare" when args.Length == 2 => await Prepare(args[1]),
				"run" when args.Length == 2 => await Run(args[1]),
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
		if (previous.Results != null
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
				previous.Results.Status);
			CfdResultSummary enriched = previous.Results with
			{
				Iterations = Math.Max(previous.Results.Iterations, ReadIterationCount(Path.Combine(work, "results", "run.log"))),
				DensityConsistencyMaximumRelativeError = cachedMetrics.DensityConsistencyMaximumRelativeError,
				Residuals = previous.Results.Residuals.Count > 0
					? previous.Results.Residuals
					: ReadResiduals(Path.Combine(work, "results", "run.log")),
			};
			document = document with { Results = enriched };
			CfdCaseStore.Save(casePath, document);
			Console.WriteLine($"CacheHit: {document.SolveHash}");
			return enriched.Status == CfdRunStatus.Converged ? 0 : 2;
		}
		WslOpenFoamEnvironment environment = await WslOpenFoamEnvironment.DetectAsync();
		WslOpenFoamRunner runner = new(environment);
		OpenFoamRunResult result = await runner.RunAsync(
			work,
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
			Results = summary,
		};
		CfdCaseStore.Save(casePath, document);
		Console.WriteLine($"{result.Status}: {result.LogPath}");
		return result.Status == CfdRunStatus.Converged ? 0 : 2;
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
		if (document.Results == null)
			throw new InvalidOperationException("The CFD case has no persisted results to view.");
		string packagePath = Path.GetFullPath(Path.Combine(
			Path.GetDirectoryName(fullCase)!,
			document.SourcePackagePath));
		LoadedGasPackage package = GasPackageReader.Load(packagePath);
		GasPathManifest path = package.Manifest.Paths.Single(item => item.Id == document.SelectedGasPathId);
		VerifiedOpenFoamResults results = OpenFoamResultVerifier.Verify(fullCase + ".work\\results", path);
		using CfdViewerApplication viewer = new(null, results, document.Results);
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
		WslOpenFoamEnvironment environment = await WslOpenFoamEnvironment.DetectAsync();
		string meshHash = CfdCaseStore.ComputeMeshHash(document, environment.Fingerprint);
		string solveHash = CfdCaseStore.ComputeSolveHash(document, environment.Fingerprint, meshHash);
		document = document with
		{
			Toolchain = environment.Fingerprint,
			MeshHash = meshHash,
			SolveHash = solveHash,
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
		Console.Error.WriteLine("FishGfx.CFD prepare <case.fgcfd>");
		Console.Error.WriteLine("FishGfx.CFD run <case.fgcfd>");
		Console.Error.WriteLine("FishGfx.CFD view <gas.fggas> [path-id]");
		Console.Error.WriteLine("FishGfx.CFD view <case.fgcfd>");
		return 64;
	}
}
