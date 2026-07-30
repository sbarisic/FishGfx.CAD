using FishGfx.CFD;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace FishGfx.CFD.Tests;

public sealed class CfdModelTests
{
	[Fact]
	public void IdealAirPresetIsThermodynamicallyValid()
	{
		CfdFluidPreset.IdealAirExhaustV1.Validate();
		Assert.InRange(CfdFluidPreset.IdealAirExhaustV1.MolecularWeight, 28.9, 29.1);
	}

	[Theory]
	[InlineData(10, 0.05)]
	[InlineData(40, 0.2)]
	[InlineData(200, 0.5)]
	public void FirstLayerDefaultIsGeometryScaledAndClamped(double diameter, double expected)
	{
		Assert.Equal(expected, CfdMeshSettings.DefaultFirstLayerThickness(diameter), 12);
	}

	[Fact]
	public void PreviewMeshRemovesLayersAndReducesResolutionWithoutChangingProductionDefaults()
	{
		CfdMeshSettings preview = CfdMeshQualityPresets.Corsa(CfdMeshQuality.Preview);
		CfdMeshSettings production = CfdMeshQualityPresets.Corsa(CfdMeshQuality.Production);
		preview.Validate();
		production.Validate();
		Assert.Equal(8, preview.CellsAcrossSmallestInlet);
		Assert.Equal(0, preview.OpeningRefinementLevel);
		Assert.Equal(0, preview.LayerCount);
		Assert.Equal(250_000, preview.MaximumCells);
		Assert.Equal(18, production.CellsAcrossSmallestInlet);
		Assert.Equal(3, production.LayerCount);
		CfdEngineTransientSettings transient = CfdMeshQualityPresets.CorsaTransient(
			new CfdEngineTransientSettings(), CfdMeshQuality.Preview);
		Assert.Equal(0.5, transient.MaximumCourantNumber);
		Assert.Equal(250, transient.MaximumVelocityMetersPerSecond);
		Assert.Equal(CfdTransientTimeScheme.Euler, transient.TimeScheme);
		Assert.Equal(4, transient.SolverAlignmentDegrees);
		Assert.Equal(1, transient.MaximumTimeStepDegrees);
		Assert.Equal(2, transient.PimpleOuterCorrectors);
		Assert.Equal(1, transient.PimplePressureCorrectors);
		Assert.Equal(0, transient.PimpleNonOrthogonalCorrectors);
		Assert.Equal(2, transient.MinimumCycles);
		Assert.Equal(2, transient.MaximumCycles);
		CfdCaptureSettings capture = CfdMeshQualityPresets.CorsaCapture(
			new CfdCaptureSettings(), CfdMeshQuality.Preview);
		Assert.Equal(4, capture.RetainedOutputAngleDegrees);
		CfdSolverSettings solver = CfdMeshQualityPresets.CorsaSolver(
			new CfdSolverSettings());
		Assert.Equal(CfdSolverSettings.CorsaEstimatedMassFlowKgPerSecond, solver.TotalMassFlowKgPerSecond);
	}

	[Fact]
	public void ToolchainChangesInvalidateMeshAndSolveHashes()
	{
		CfdCaseDocument document = new() { SourceHash = new string('a', 64) };
		CfdToolchainFingerprint first = Toolchain("14-20260724");
		CfdToolchainFingerprint second = Toolchain("14-20260725");
		string firstMesh = CfdCaseStore.ComputeMeshHash(document, first);
		string secondMesh = CfdCaseStore.ComputeMeshHash(document, second);
		Assert.NotEqual(firstMesh, secondMesh);
		Assert.NotEqual(
			CfdCaseStore.ComputeSolveHash(document, first, firstMesh),
			CfdCaseStore.ComputeSolveHash(document, second, secondMesh));
	}

	[Fact]
	public void FailedRuntimeRetentionDoesNotInvalidateSolveHash()
	{
		CfdCaseDocument discard = new() { SourceHash = new string('a', 64) };
		CfdCaseDocument retain = discard with
		{
			Solver = discard.Solver with { RetainFailedRuntime = true },
		};
		CfdToolchainFingerprint toolchain = Toolchain("14");
		string meshHash = CfdCaseStore.ComputeMeshHash(discard, toolchain);
		Assert.Equal(
			CfdCaseStore.ComputeSolveHash(discard, toolchain, meshHash),
			CfdCaseStore.ComputeSolveHash(retain, toolchain, meshHash));
	}

	[Fact]
	public void ComputeBackendChangesSolveHashButNotMeshHash()
	{
		CfdCaseDocument gpu = new() { SourceHash = new string('a', 64) };
		CfdCaseDocument cpu = gpu with
		{
			Compute = CfdComputeSettings.For(CfdComputeBackend.CpuNative),
		};
		CfdToolchainFingerprint cpuToolchain = Toolchain("14") with
		{
			ComputeBackend = CfdComputeBackend.CpuNative,
			SolverProfile = CfdComputeSettings.CpuSolverProfile,
		};
		CfdToolchainFingerprint gpuToolchain = cpuToolchain with
		{
			ComputeBackend = CfdComputeBackend.AmdGpuPetsc,
			SolverProfile = CfdComputeSettings.AmdGpuSolverProfile,
			GpuArchitecture = "gfx1201",
			PetscGitCommit = "abc123",
			AdapterSha256 = new string('c', 64),
		};
		string cpuMesh = CfdCaseStore.ComputeMeshHash(cpu, cpuToolchain);
		string gpuMesh = CfdCaseStore.ComputeMeshHash(gpu, gpuToolchain);
		Assert.Equal(cpuMesh, gpuMesh);
		Assert.NotEqual(
			CfdCaseStore.ComputeSolveHash(cpu, cpuToolchain, cpuMesh),
			CfdCaseStore.ComputeSolveHash(gpu, gpuToolchain, gpuMesh));
	}

	[Fact]
	public void V3CaseMigratesToRequiredAmdGpuWithoutDiscardingResults()
	{
		string path = Path.Combine(Path.GetTempPath(), $"fishgfx-cfd-v3-{Guid.NewGuid():N}.fgcfd");
		try
		{
			CfdCaseDocument source = new()
			{
				Results = new CfdCaseResults
				{
					Steady = new CfdResultSummary { Status = CfdRunStatus.Converged },
				},
			};
			JsonObject root = JsonSerializer.SerializeToNode(source, CfdJson.Options)!.AsObject();
			root["version"] = 3;
			root.Remove("compute");
			File.WriteAllText(path, root.ToJsonString(CfdJson.Options));

			CfdCaseDocument migrated = CfdCaseStore.Load(path);
			Assert.Equal(4, migrated.Version);
			Assert.Equal(CfdComputeBackend.AmdGpuPetsc, migrated.Compute.Backend);
			Assert.Equal(CfdRunStatus.Converged, migrated.Results.Steady!.Status);
		}
		finally { File.Delete(path); }
	}

	private static CfdToolchainFingerprint Toolchain(string version) => new(
		"Foundation", version, "14", "linux64GccDPInt32Opt", "/opt/openfoam14/etc/bashrc",
		new string('b', 64), OpenFoamCaseGenerator.TemplateVersion, 1, 1, 1);
}
