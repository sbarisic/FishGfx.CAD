using System.Numerics;
using System.Text.Json;
using FishGfx.CFD;
using Xunit;

namespace FishGfx.CFD.Tests;

public sealed class CfdTurbineMapTests
{
	[Fact]
	public void A14NetOperatingPointResolvesSpecifiedMassFlow()
	{
		CfdEngineOperatingPoint point = new();
		point.Validate();
		Assert.Equal(0.004, point.FuelMassFlowKgPerSecond, 12);
		Assert.Equal(0.04998, point.AirMassFlowKgPerSecond, 12);
		Assert.Equal(0.05398, point.ExhaustMassFlowKgPerSecond, 12);
		double eventMass = point.ExhaustMassFlowKgPerSecond * (120.0 / 3500.0) / 4;
		Assert.Equal(0.0004626857142857143, eventMass, 15);
	}

	[Fact]
	public void GarrettCurveIsStrictAndEndsAtSynthetic102PercentLimit()
	{
		CfdTurbineMapPreset preset = CfdTurbineMaps.GarrettG25550Point49ProxyV1;
		CfdTurbineBoundarySettings settings = new()
		{
			Mode = CfdOutletBoundaryMode.TurbineMapImpedance,
			PresetId = preset.Id,
			WastegateClosed = true,
		};
		CfdTurbineCurvePoint[] curve = CfdTurbineMaps.BuildFanCurve(
			preset,
			CfdFluidPreset.IdealAirExhaustV1,
			settings);
		Assert.Equal(0, curve[0].VolumeFlowCubicMetersPerSecond);
		Assert.Equal(0, curve[0].FanCurvePressurePa);
		Assert.All(curve.Zip(curve.Skip(1)), pair =>
			Assert.True(pair.First.VolumeFlowCubicMetersPerSecond < pair.Second.VolumeFlowCubicMetersPerSecond));
		CfdTurbineCurvePoint finalPublished = curve.Last(value => value.Published);
		Assert.Equal(finalPublished.VolumeFlowCubicMetersPerSecond * 1.02,
			curve[^1].VolumeFlowCubicMetersPerSecond, 12);
		Assert.False(curve[^1].Published);
		Assert.True(curve[^1].FanCurvePressurePa < finalPublished.FanCurvePressurePa);
		string[] solverCsv = CfdTurbineMaps.OpenFoamSolverCsv(curve)
			.Split('\n', StringSplitOptions.RemoveEmptyEntries);
		double solverLimitPressure = double.Parse(
			solverCsv[^1].Split(',')[1],
			System.Globalization.CultureInfo.InvariantCulture);
		Assert.Equal(finalPublished.FanCurvePressurePa, solverLimitPressure, 12);
		Assert.Equal(2048, preset.SourceImageWidth);
		Assert.Equal("Unspecified by source image", preset.PublishedPressureRatioDefinition);
		Assert.All(preset.RawPoints, point =>
		{
			Assert.True(point.CorrectedMassFlowKgPerSecond > 0);
			Assert.InRange(point.PressureRatio, 1, 4);
		});
		Assert.Equal(0.0018507428571428572, new CfdEngineOperatingPoint().CycleMassKg, 15);
		Assert.Equal(0.0004626857142857143, new CfdEngineOperatingPoint().CylinderEventMassKg, 15);
		double previewRatio = CfdTurbineMaps.EstimatePressureRatioForActualMassFlow(
			preset,
			settings,
			new CfdEngineOperatingPoint().ExhaustMassFlowKgPerSecond);
		Assert.InRange(previewRatio, 1.30, 1.32);
	}

	[Fact]
	public void TurbinePhysicsInvalidatesSolveButNotMeshHash()
	{
		CfdCaseDocument wave = TransientCase();
		CfdCaseDocument turbine = wave with
		{
			OperatingPoint = new CfdEngineOperatingPoint(),
			TurbineBoundary = new CfdTurbineBoundarySettings
			{
				Mode = CfdOutletBoundaryMode.TurbineMapImpedance,
				PresetId = CfdTurbineBoundarySettings.GarrettG25550PresetId,
				WastegateClosed = true,
			},
		};
		CfdToolchainFingerprint toolchain = new(
			"Foundation", "OpenFOAM-14", "14", "linux64GccDPInt32Opt", "/opt/openfoam14/etc/bashrc",
			new string('a', 64), OpenFoamCaseGenerator.TransientTemplateVersion, 3, 1, 8);
		string waveMesh = CfdCaseStore.ComputeMeshHash(wave, toolchain);
		string turbineMesh = CfdCaseStore.ComputeMeshHash(turbine, toolchain);
		Assert.Equal(waveMesh, turbineMesh);
		Assert.NotEqual(
			CfdCaseStore.ComputeSolveHash(wave, toolchain, waveMesh),
			CfdCaseStore.ComputeSolveHash(turbine, toolchain, turbineMesh));
	}

	[Fact]
	public void V2TransientMigratesToWaveTransmissiveAndRetainsResultMetadata()
	{
		string path = Path.Combine(Path.GetTempPath(), $"fishgfx-v2-{Guid.NewGuid():N}.fgcfd");
		try
		{
			CfdCaseDocument source = TransientCase() with
			{
				Version = 2,
				Results = new CfdCaseResults
				{
					TransientSummary = new CfdTransientResultSummary
					{
						Status = CfdRunStatus.MaximumCyclesWithoutPeriodicity,
						ModelLabel = CfdEngineTransientSettings.WaveBoundaryModelLabel,
					},
				},
			};
			JsonElement serialized = JsonSerializer.SerializeToElement(source, CfdJson.Options);
			Dictionary<string, JsonElement> properties = serialized.EnumerateObject()
				.Where(value => value.Name is not ("operatingPoint" or "turbineBoundary"))
				.ToDictionary(value => value.Name, value => value.Value);
			File.WriteAllText(path, JsonSerializer.Serialize(properties, CfdJson.Options));
			CfdCaseDocument migrated = CfdCaseStore.Load(path);
			Assert.Equal(4, migrated.Version);
			Assert.Equal(CfdOutletBoundaryMode.WaveTransmissiveFarField, migrated.TurbineBoundary.Mode);
			Assert.Equal(CfdComputeBackend.AmdGpuPetsc, migrated.Compute.Backend);
			Assert.Equal(CfdEngineTransientSettings.WaveBoundaryModelLabel,
				migrated.Results.TransientSummary!.ModelLabel);
		}
		finally { File.Delete(path); }
	}

	[Fact]
	public void BoundaryBvhDetectsInsideAndStopsWallCrossing()
	{
		VtkVector[] points =
		[
			new(-1, -1, -1), new(1, -1, -1), new(1, 1, -1), new(-1, 1, -1),
			new(-1, -1, 1), new(1, -1, 1), new(1, 1, 1), new(-1, 1, 1),
		];
		VtkCell[] faces =
		[
			new(9, [0, 3, 2, 1]), new(9, [4, 5, 6, 7]),
			new(9, [0, 1, 5, 4]), new(9, [1, 2, 6, 5]),
			new(9, [2, 3, 7, 6]), new(9, [3, 0, 4, 7]),
		];
		LegacyVtkDataSet surface = new() { Points = points, Cells = faces };
		CfdBoundaryBvh bvh = new([new CfdBoundaryPatch("walls", "walls", surface)], 0.1f);
		Assert.True(bvh.IsInside(Vector3.Zero));
		Assert.False(bvh.IsInside(new Vector3(2, 0, 0)));
		Assert.True(bvh.TryIntersectSegment(Vector3.Zero, new Vector3(2, 0, 0), out CfdBoundaryHit hit));
		Assert.Equal("walls", hit.Role);
		Assert.Equal(1, hit.Position.X, 5);
	}

	[Fact]
	public void VelocitySamplerExpandsPastWallBlockedNearestCandidates()
	{
		List<VtkVector> samples = [];
		List<VtkVector> velocities = [];
		for (int index = 0; index < 12; ++index)
		{
			double offset = (index - 5.5) * 0.002;
			samples.Add(new(0.2, offset, -offset));
			velocities.Add(new(0, 10, 0));
		}
		for (int index = 0; index < 12; ++index)
		{
			double offset = (index - 5.5) * 0.002;
			samples.Add(new(-0.3, offset, -offset));
			velocities.Add(new(1, 0, 0));
		}
		LegacyVtkDataSet wall = new()
		{
			Points =
			[
				new(0.1, -1, -1), new(0.1, 1, -1),
				new(0.1, 1, 1), new(0.1, -1, 1),
			],
			Cells = [new(9, [0, 1, 2, 3])],
		};
		CfdSpatialSampleIndex spatial = new(samples.ToArray(), 0.1f);
		CfdBoundaryBvh boundary = new([new CfdBoundaryPatch("wall", "walls", wall)], spatial.CellSize);
		CfdVelocityFrameSampler sampler = new(spatial, velocities.ToArray(), boundary);

		Assert.True(sampler.TrySample(Vector3.Zero, out Vector3 velocity));
		Assert.True(velocity.X > 0.99f);
		Assert.True(MathF.Abs(velocity.Y) < 1e-5f);
	}

	[Fact]
	public void AcceptedFrameStillRejectsFlowBeyondSyntheticLimit()
	{
		CfdTurbineBoundarySettings settings = new()
		{
			Mode = CfdOutletBoundaryMode.TurbineMapImpedance,
			PresetId = CfdTurbineBoundarySettings.GarrettG25550PresetId,
			WastegateClosed = true,
		};
		CfdTurbineCurvePoint[] curve = CfdTurbineMaps.BuildFanCurve(
			CfdTurbineMaps.GarrettG25550Point49ProxyV1,
			CfdFluidPreset.IdealAirExhaustV1,
			settings);
		LegacyVtkDataSet outlet = new()
		{
			Points = [new(0, 0, 0)],
			Cells = [],
		};
		outlet.PointScalars["rho"] = [1];
		outlet.PointScalars["p"] = [101325];
		double excessiveMassFlow = curve[^1].VolumeFlowCubicMetersPerSecond * 1.01;
		Assert.Throws<InvalidDataException>(() => CfdTurbineDiagnostics.Calculate(
			0,
			0,
			excessiveMassFlow,
			outlet,
			curve,
			settings));
		(CfdTurbineFrameDiagnostic preview, _) = CfdTurbineDiagnostics.Calculate(
			0,
			0,
			excessiveMassFlow,
			outlet,
			curve,
			settings,
			32000);
		Assert.Equal(CfdTurbineMapRangeState.AbovePublishedRange, preview.RangeState);
		Assert.Equal(32000, preview.EstimatedPressureDropPa);
	}

	private static CfdCaseDocument TransientCase() => new()
	{
		SourceHash = new string('c', 64),
		AnalysisMode = CfdAnalysisMode.EngineTransient,
		EngineTransient = new CfdEngineTransientSettings
		{
			CylinderAssignments = [new(1, "one"), new(2, "two"), new(3, "three"), new(4, "four")],
		},
	};
}
