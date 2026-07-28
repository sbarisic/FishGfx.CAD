using FishGfx.Cad;

namespace FishGfx.ManifoldCad;

internal static class HeadlessCfdExport
{
	internal static void Export(string projectPath, string packagePath)
	{
		string fullProjectPath = Path.GetFullPath(projectPath);
		string fullPackagePath = Path.GetFullPath(packagePath);
		CadProjectPackage package = CadProjectArchive.Load(fullProjectPath);
		CadDocument document = CadDocument.CreateAsync().GetAwaiter().GetResult();
		string temporaryXcaf = Path.Combine(
			Path.GetTempPath(),
			$"fishgfx-cfd-export-{Guid.NewGuid():N}.xbf");
		try
		{
			File.WriteAllBytes(temporaryXcaf, package.ModelDocument);
			document.LoadXcafAsync(temporaryXcaf).GetAwaiter().GetResult();
			ManifoldCadApplication.RestoreResolvedMateSelectorsAsync(document, package.Project)
				.GetAwaiter().GetResult();
			RebuildPublishedGasGeometry(document, package.Project);
			CadGasPackageInfo result = document.ExportGasPackageAsync(fullPackagePath)
				.GetAwaiter().GetResult();
			Console.WriteLine(
				$"FISHGFX_CFD_EXPORT_OK package={result.Path} "
					+ $"packageHash={result.PackageFileHash} geometryHash={result.GeometryStepHash}");
		}
		finally
		{
			document.DisposeAsync().AsTask().GetAwaiter().GetResult();
			File.Delete(temporaryXcaf);
		}
	}

	private static void RebuildPublishedGasGeometry(CadDocument document, ManifoldProject project)
	{
		HashSet<Guid> collectorMembers = project.CollectorSystems
			.SelectMany(system => system.Inlets)
			.Select(inlet => inlet.Binding.RunnerId)
			.ToHashSet();
		foreach (CadRunner runner in project.Runners.Where(runner => !collectorMembers.Contains(runner.Id)))
		{
			RunnerEvaluationResult evaluation = project.EvaluateRunnerAsync(document, runner)
				.GetAwaiter().GetResult();
			RequireSuccessful(evaluation, runner.Name);
			document.BuildRunnerAsync(runner, evaluation).GetAwaiter().GetResult();
		}

		foreach (CadCollectorSystem system in project.CollectorSystems)
		{
			Dictionary<Guid, RunnerEvaluationResult> members = new();
			foreach (CadCollectorInlet inlet in system.Inlets)
			{
				CadRunner runner = project.Runners.Single(item => item.Id == inlet.Binding.RunnerId);
				RunnerEvaluationResult evaluation = project.EvaluateRunnerAsync(document, runner)
					.GetAwaiter().GetResult();
				RequireSuccessful(evaluation, runner.Name);
				members.Add(runner.Id, evaluation);
			}

			system.OutletProfile = CadCollectorSystem.AreaPreservingOutletProfile(
				members.Values.Select(value => value.Chain.ActiveProfile),
				system.OutletProfile.WallThicknessMillimetres);
			bool staged = false;
			try
			{
				document.BeginCollectorSystemBuildAsync(system).GetAwaiter().GetResult();
				staged = true;
				foreach (CadCollectorInlet inlet in system.Inlets)
				{
					CadRunner runner = project.Runners.Single(item => item.Id == inlet.Binding.RunnerId);
					document.BuildRunnerAsync(runner, members[runner.Id], system)
						.GetAwaiter().GetResult();
				}
				document.BuildCollectorSystemAsync(system, members).GetAwaiter().GetResult();
				document.CommitCollectorSystemBuildAsync(system.Id, system.GenerationRevision)
					.GetAwaiter().GetResult();
				staged = false;
			}
			finally
			{
				if (staged)
				{
					document.AbortCollectorSystemBuildAsync(system.Id, system.GenerationRevision)
						.GetAwaiter().GetResult();
				}
			}
		}
	}

	private static void RequireSuccessful(RunnerEvaluationResult evaluation, string runnerName)
	{
		if (!evaluation.Success)
		{
			throw new InvalidOperationException(
				$"Runner '{runnerName}' could not be evaluated: "
					+ string.Join("; ", evaluation.Diagnostics.Select(item => item.Message)));
		}
	}
}
