using FishGfx.CFD;
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

	private static CfdToolchainFingerprint Toolchain(string version) => new(
		"Foundation", version, "14", "linux64GccDPInt32Opt", "/opt/openfoam14/etc/bashrc",
		new string('b', 64), OpenFoamCaseGenerator.TemplateVersion, 1, 1, 1);
}
