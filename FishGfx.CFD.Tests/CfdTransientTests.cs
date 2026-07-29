using System.Text.Json;
using FishGfx.Cad;
using FishGfx.CFD;
using Xunit;

namespace FishGfx.CFD.Tests;

public sealed class CfdTransientTests
{
	[Fact]
	public void ReadsOpenFoamMonitorHistoriesAndReportsPeriodicity()
	{
		string root = Path.Combine(Path.GetTempPath(), $"fishgfx-monitor-{Guid.NewGuid():N}");
		try
		{
			CfdEngineTransientSettings settings = new()
			{
				MaximumCycles = 2,
				MinimumCycles = 2,
				CylinderAssignments = Enumerable.Range(1, 4)
					.Select(value => new CfdCylinderAssignment(value, $"runner-{value}"))
					.ToList(),
			};
			WriteMonitor(root, "outletMassFlow", settings, (angle, cycle) => 0.1 + 0.02 * Math.Sin(angle * Math.PI / 360));
			WriteMonitor(root, "outletPressure", settings, (angle, cycle) => 101325 + 250 * Math.Cos(angle * Math.PI / 360));
			WriteMonitor(root, "domainMass", settings, (angle, cycle) => 0.002);

			CfdPeriodicityResult result = OpenFoamTransientMonitor.ReadAndCompareLastCycles(root, settings);

			Assert.True(result.Passed);
			Assert.Equal(2, result.ComparedCycle);
		}
		finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
	}

	private static void WriteMonitor(
		string resultRoot,
		string name,
		CfdEngineTransientSettings settings,
		Func<double, int, double> value)
	{
		string directory = Path.Combine(resultRoot, "postProcessing", name, "0");
		Directory.CreateDirectory(directory);
		using StreamWriter writer = new(Path.Combine(directory, "values.dat"));
		writer.WriteLine("# Time value");
		for (int cycle = 0; cycle < settings.MaximumCycles; ++cycle)
		for (int sample = 0; sample <= 720 / settings.SolverAlignmentDegrees; ++sample)
		{
			double angle = sample * settings.SolverAlignmentDegrees;
			double time = cycle * settings.CycleDurationSeconds + angle * settings.SecondsPerDegree;
			writer.WriteLine(FormattableString.Invariant($"{time:R} {value(angle % 720, cycle):R}"));
		}
	}
	[Fact]
	public void CorsaPulseRepeatsAndHasExactDiscreteMassAfterStartupRamp()
	{
		CfdEngineTransientSettings settings = Settings(maximumCycles: 5);
		CfdSolverSettings solver = new();
		CfdTransientPulseSet pulse = CfdTransientPulseGenerator.Generate(settings, solver);
		int samplesPerCycle = checked((int)Math.Round(720 / settings.PulseTableStepDegrees));
		CfdCylinderPulseTable cylinder = pulse.Cylinders.Single(value => value.CylinderNumber == 1);
		Assert.Equal(0.0008571428571428572, cylinder.EventMassKg, 14);
		Assert.Equal(cylinder.EventMassKg,
			CfdTransientPulseGenerator.IntegrateMassOverCycle(
				cylinder,
				samplesPerCycle,
				settings.StartupRampCycles), 13);
		Assert.InRange(cylinder.PeakMassFlowKgPerSecond, 0.176, 0.177);
		Assert.Equal(cylinder.MassFlow[0].Value, cylinder.MassFlow[samplesPerCycle * 5].Value, 14);
		Assert.Equal(0, cylinder.MassFlow[^1].Value);
		Assert.True(cylinder.MassFlow[^1].TimeSeconds > cylinder.MassFlow[^2].TimeSeconds);
		for (int cycle = settings.StartupRampCycles + 1; cycle < settings.MaximumCycles; ++cycle)
		{
			for (int index = 0; index <= samplesPerCycle; index += 137)
				Assert.Equal(cylinder.MassFlow[settings.StartupRampCycles * samplesPerCycle + index].Value,
					cylinder.MassFlow[cycle * samplesPerCycle + index].Value, 13);
		}
		Assert.NotEqual(cylinder.EventMassKg,
			CfdTransientPulseGenerator.IntegrateMassOverCycle(cylinder, samplesPerCycle));

		CfdEngineTransientSettings compatible = settings with
		{
			InitialisationMode = TransientInitialisationMode.CompatibleSteadyResult,
			InitialSteadySolveHash = new string('e', 64),
			InitialSteadyCaseId = Guid.NewGuid(),
		};
		CfdTransientPulseSet compatiblePulse = CfdTransientPulseGenerator.Generate(compatible, solver);
		Assert.All(compatiblePulse.Cylinders, value => Assert.Equal(0.025, value.MassFlow[0].Value, 14));
		CfdTransientPulseSet mappedPulse = CfdTransientPulseGenerator.Generate(
			compatible with { InitialisationMode = TransientInitialisationMode.MappedSteadyPreview }, solver);
		Assert.All(mappedPulse.Cylinders, value => Assert.Equal(0.025, value.MassFlow[0].Value, 14));
	}

	[Fact]
	public void HashLayersInvalidateOnlyTheirConsumers()
	{
		CfdCaseDocument document = TransientDocument();
		CfdToolchainFingerprint toolchain = Toolchain();
		string mesh = CfdCaseStore.ComputeMeshHash(document, toolchain);
		string solve = CfdCaseStore.ComputeSolveHash(document, toolchain, mesh);
		string capture = CfdCaseStore.ComputeCaptureHash(document, solve);
		string result = CfdCaseStore.ComputeResultHash(document, capture);

		CfdCaseDocument outputChanged = document with
		{
			Results = new CfdCaseResults
			{
				Transient = new("case.results/transient.fgflow", new string('a', 64), 3, 360, new()),
			},
		};
		Assert.Equal(solve, CfdCaseStore.ComputeSolveHash(outputChanged, toolchain, mesh));

		CfdCaseDocument captureChanged = document with
		{
			Capture = document.Capture with { RetainedOutputAngleDegrees = 4 },
		};
		Assert.Equal(solve, CfdCaseStore.ComputeSolveHash(captureChanged, toolchain, mesh));
		Assert.NotEqual(capture, CfdCaseStore.ComputeCaptureHash(captureChanged, solve));

		CfdCaseDocument resultChanged = document with
		{
			ResultStorage = document.ResultStorage with { CompressionQuality = 7 },
		};
		Assert.Equal(capture, CfdCaseStore.ComputeCaptureHash(resultChanged, solve));
		Assert.NotEqual(result, CfdCaseStore.ComputeResultHash(resultChanged, capture));
	}

	[Fact]
	public void PeriodicitySeparatesPressureMeanAndFluctuation()
	{
		CfdEngineTransientSettings settings = Settings();
		CfdCycleMonitorSample[] previous = Samples(0);
		CfdCycleMonitorSample[] sameShapeDifferentMean = Samples(20);
		CfdPeriodicityResult result = CfdTransientPeriodicity.Compare(3, previous, sameShapeDifferentMean, settings);
		Assert.InRange(result.PressureFluctuationNrmse, 0, 1e-12);
		Assert.Equal(20, result.MeanPressureChangePa, 9);
		Assert.False(result.Passed);
	}

	[Fact]
	public void TransientMetricsHandleUnavailableAndIntegratedPressureLoss()
	{
		Assert.Equal(CfdTransientMetricState.Unavailable,
			CfdTransientMetricCalculator.Classify(1e-8, 0.1, 1e-5));
		Assert.Equal(CfdTransientMetricState.ReverseFlow,
			CfdTransientMetricCalculator.Classify(0.1, -0.01, 1e-5));
		CfdTransientFluxSample[] samples =
		[
			new(0, 1, 1, 110000, 100000),
			new(1, 3, 1, 130000, 100000),
			new(2, 1, 1, 110000, 100000),
		];
		Assert.Equal(25000, CfdTransientMetricCalculator.CycleAveragePressureLoss(samples, 1e-5)!.Value, 8);
		VerifiedOpenFoamResults frame = Frame(1);
		(CfdTransientFrameMetric closedMetric, _) = CfdMetrics.CalculateTransientFrame(
			0,
			0,
			0,
			frame.Boundaries,
			CfdFluidPreset.IdealAirExhaustV1,
			1e-5,
			new HashSet<string>(["inlet"], StringComparer.Ordinal));
		Assert.Equal(1, closedMetric.NominallyClosedInletCount);
		Assert.InRange(closedMetric.NominallyClosedReverseFlowAreaFraction, 0, 1);
		Assert.True(closedMetric.NominallyClosedTangentialVelocityAreaWeightedMeanMps > 0);
	}

	[Fact]
	public void StreamlineCancellationReturnsWithoutThrowingOrPublishingPartialLines()
	{
		VtkVector[] points =
		[
			new(0, 0, 0),
			new(1, 0, 0),
			new(2, 0, 0),
		];
		VtkVector[] velocities =
		[
			new(1, 0, 0),
			new(1, 0, 0),
			new(1, 0, 0),
		];
		CfdSpatialSampleIndex index = new(points, 1);
		using CancellationTokenSource cancellation = new();
		cancellation.Cancel();

		CfdStreamlineResult result = CfdStreamlineTracer.Trace(
			index,
			velocities,
			[new System.Numerics.Vector3(0, 0, 0)],
			cancellation.Token,
			17,
			"velocity-checksum");

		Assert.True(result.IsCanceled);
		Assert.Empty(result.Lines);
		Assert.Equal(17, result.FrameIndex);
		Assert.Equal("velocity-checksum", result.VelocityChecksum);
	}

	[Fact]
	public async Task FgFlowRoundTripsLazyFramesAndAssociationRanges()
	{
		string directory = Path.Combine(Path.GetTempPath(), $"fishgfx-fgflow-{Guid.NewGuid():N}");
		Directory.CreateDirectory(directory);
		try
		{
			string path = Path.Combine(directory, "transient.fgflow");
			List<CfdFlowFrameSource> frames =
			[
				new(0, 0, 0, Frame(1)),
				new(1, 0.001, 2, Frame(2)),
				new(2, 0.002, 4, Frame(3)),
				new(3, 0.003, 6, Frame(4)),
			];
			string hash = FgFlowWriter.Write(path, new string('a', 64), new string('b', 64), 3, frames,
				new CfdResultStorageSettings { MaximumVolumeSamples = 2 });
			using FgFlowResultSequence sequence = new(path, hash);
			Assert.Equal(4, sequence.FrameCount);
			Assert.Contains("p/volume", sequence.Ranges.Keys);
			Assert.Contains("p/walls", sequence.Ranges.Keys);
			Assert.Contains("p/openings", sequence.Ranges.Keys);
			CfdResultFrame loaded = await sequence.LoadFrameAsync(2, CancellationToken.None);
			Assert.Equal(3, loaded.Results.Volume.PointScalars["p"][0]);
			Assert.Equal(2, loaded.Results.Volume.Points.Length);
			Assert.Equal(4, sequence.GetFrameInfo(2).CrankAngleDegrees);
		}
		finally { Directory.Delete(directory, true); }
	}

	[Fact]
	public void V1CaseMigratesToSteadyV2()
	{
		string path = Path.Combine(Path.GetTempPath(), $"fishgfx-case-{Guid.NewGuid():N}.fgcfd");
		try
		{
			File.WriteAllText(path, """
				{"schema":"fishgfx.cfd-case","version":1,"sourcePackagePath":"gas.fggas",
				"packageFileHash":"x","sourceHash":"y","selectedGasPathId":"z",
				"manualClassificationOverrides":{},"matchingDiagnostics":[],"mesh":{},"solver":{},
				"results":{"status":1,"iterations":5,"residuals":[]}}
				""");
			CfdCaseDocument document = CfdCaseStore.Load(path);
			Assert.Equal(2, document.Version);
			Assert.Equal(CfdAnalysisMode.Steady, document.AnalysisMode);
			Assert.Equal(5, document.Results.Steady!.Iterations);
		}
		finally { File.Delete(path); }
	}

	private static CfdEngineTransientSettings Settings(int maximumCycles = 6) => new()
	{
		MaximumCycles = maximumCycles,
		CylinderAssignments =
		[
			new(1, "one"), new(2, "two"), new(3, "three"), new(4, "four"),
		],
	};

	private static CfdCaseDocument TransientDocument() => new()
	{
		SourceHash = new string('c', 64),
		AnalysisMode = CfdAnalysisMode.EngineTransient,
		EngineTransient = Settings(),
	};

	private static CfdToolchainFingerprint Toolchain() => new(
		"Foundation", "OpenFOAM-14", "14", "linux64GccDPInt32Opt", "/opt/openfoam14/etc/bashrc",
		new string('d', 64), OpenFoamCaseGenerator.TransientTemplateVersion, 3, 1, 2);

	private static CfdCycleMonitorSample[] Samples(double pressureOffset) => Enumerable.Range(0, 360)
		.Select(index =>
		{
			double angle = index * 2;
			double wave = Math.Sin(angle * Math.PI / 180);
			return new CfdCycleMonitorSample(angle, 0.1 + wave * 0.01, 101325 + pressureOffset + wave * 500, 0.001);
		}).ToArray();

	private static VerifiedOpenFoamResults Frame(double value)
	{
		LegacyVtkDataSet volume = Data(value, false);
		LegacyVtkDataSet wall = Data(value + 10, true);
		LegacyVtkDataSet inlet = Data(value + 20, true);
		LegacyVtkDataSet outlet = Data(value + 30, true);
		return new(volume,
		[
			new("walls", "walls", wall),
			new("inlet", "inlet", inlet),
			new("outlet", "outlet", outlet),
		]);
	}

	private static LegacyVtkDataSet Data(double value, bool surface)
	{
		LegacyVtkDataSet result = new()
		{
			Points = [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)],
			Cells = surface ? [new VtkCell(5, [0, 1, 2])] : [],
		};
		foreach (string field in new[] { "p", "T", "rho", "Ma", "yPlus" })
			result.PointScalars[field] = [value, value + 1, value + 2];
		result.PointVectors["U"] = [new(value, 0, 0), new(value, 1, 0), new(value, 0, 1)];
		return result;
	}
}
