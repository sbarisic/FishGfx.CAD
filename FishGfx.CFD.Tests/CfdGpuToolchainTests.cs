using FishGfx.CFD;
using Xunit;

namespace FishGfx.CFD.Tests;

public sealed class CfdGpuToolchainTests
{
	[Fact]
	public void ValidManifestAndDeviceSmokeAreAccepted()
	{
		CfdGpuToolchainManifest manifest = Manifest();
		manifest.Validate("14", "linux64GccDPInt32Opt");
		new CfdGpuSmokeResult
		{
			Schema = CfdGpuSmokeResult.SchemaName,
			AdapterLoaded = true,
			PetscHipActive = true,
			HypreHipActive = true,
			DeviceIndex = 0,
			DeviceName = "AMD Radeon RX 9070 XT",
			DevicePciAddress = "0000:00:08.1",
			DeviceArchitecture = "gfx1201",
			Iterations = 5,
			InitialResidual = 1,
			FinalResidual = 1e-10,
		}.Validate(manifest, 0);
	}

	[Theory]
	[InlineData("single", 32)]
	[InlineData("double", 64)]
	public void RejectsWrongPetscNumericAbi(string precision, int indexBits)
	{
		CfdGpuToolchainManifest manifest = Manifest() with
		{
			PetscPrecision = precision,
			PetscIndexBits = indexBits,
		};
		Assert.Throws<InvalidDataException>(() => manifest.Validate("14", "linux64GccDPInt32Opt"));
	}

	[Fact]
	public void RejectsSmokeThatDidNotUseHypreOnGpu()
	{
		CfdGpuToolchainManifest manifest = Manifest();
		CfdGpuSmokeResult smoke = new()
		{
			Schema = CfdGpuSmokeResult.SchemaName,
			AdapterLoaded = true,
			PetscHipActive = true,
			HypreHipActive = false,
			DeviceIndex = 0,
			DeviceName = "AMD Radeon RX 9070 XT",
			DevicePciAddress = "0000:00:08.1",
			DeviceArchitecture = "gfx1201",
			Iterations = 5,
			InitialResidual = 1,
			FinalResidual = 1e-10,
		};
		Assert.Throws<InvalidDataException>(() => smoke.Validate(manifest, 0));
	}

	private static CfdGpuToolchainManifest Manifest() => new()
	{
		WmOptions = "linux64GccDPInt32Opt",
		OpenFoamEnvironmentScriptPath = "/opt/openfoam14/etc/bashrc",
		OpenFoamEnvironmentScriptSha256 = new string('c', 64),
		RocmVersion = "7.2",
		HipVersion = "7.2",
		GpuName = "AMD Radeon RX 9070 XT",
		GpuPciAddress = "0000:00:08.1",
		GpuArchitectures = ["gfx1201"],
		PetscGitCommit = "0123456789abcdef",
		PetscConfigurationSha256 = new string('a', 64),
		PetscScalarType = "real",
		PetscPrecision = "double",
		PetscIndexBits = 32,
		HypreVersion = "2.33",
		HypreConfiguration = "HIP enabled",
		AdapterGitCommit = "fedcba9876543210",
		AdapterPortVersion = "foundation14-port-v1",
		AdapterAbi = "foundation-openfoam14-linux64GccDPInt32Opt-v1",
		AdapterLibraryPath = "/opt/fishgfx/lib/libpetscFoam.so",
		AdapterSha256 = new string('b', 64),
	};
}
