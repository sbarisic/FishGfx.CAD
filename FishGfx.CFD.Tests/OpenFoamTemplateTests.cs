using FishGfx.Cad;
using FishGfx.CFD;
using Xunit;

namespace FishGfx.CFD.Tests;

public sealed class OpenFoamTemplateTests
{
	[Fact]
	public void GeneratesCompleteBoundaryMatrixAndWallOnlyLayers()
	{
		string directory = Path.Combine(Path.GetTempPath(), $"fishgfx-foam-{Guid.NewGuid():N}");
		Directory.CreateDirectory(directory);
		try
		{
			string stl = Path.Combine(directory, "source.stl");
			File.WriteAllText(stl, "solid walls\nendsolid walls\n");
			GasOpeningFingerprint fingerprint = new(
				1256.637, [0, 0, 0], [0, 0, 1], 1, 125.6637, [], [], "plane");
			GasPathManifest path = new(
				Guid.NewGuid().ToString("D"), "runner", "Runner", "FGGASPATH:V1:RUNNER:test:Runner",
				[
					new("start", "inlet_test", "inlet", "test", fingerprint),
					new("end", "outlet_test", "outlet", "test", fingerprint),
				]);
			GasPackageManifest manifest = new(
				"fishgfx.gas-patches", 1, "mm", new string('0', 64),
				CadPatchMatchingPolicy.Version1, [path]);
			LoadedGasPackage package = new("fixture", "", "", [], [], manifest);
			CfdCaseDocument document = new()
			{
				SelectedGasPathId = path.Id,
				Mesh = new CfdMeshSettings { FirstLayerThicknessMm = 0.15 },
			};
			string target = Path.Combine(directory, "case");
			OpenFoamCaseGenerator.Generate(target, document, package, new(
				stl, new(-0.1, -0.1, -0.1), new(0.1, 0.1, 0.1), new(0, 0, 0), 40));

			Dictionary<string, string[]> expected = new()
			{
				["U"] = ["flowRateInletVelocity", "pressureInletOutletVelocity", "noSlip"],
				["p"] = ["zeroGradient", "fixedValue"],
				["T"] = ["fixedValue", "inletOutlet", "zeroGradient"],
				["k"] = ["fixedValue", "inletOutlet", "kqRWallFunction"],
				["omega"] = ["fixedValue", "inletOutlet", "omegaWallFunction"],
				["nut"] = ["calculated", "nutkWallFunction"],
				["alphat"] = ["calculated", "compressible::alphatWallFunction"],
			};
			foreach ((string field, string[] values) in expected)
			{
				string text = File.ReadAllText(Path.Combine(target, "0", field));
				Assert.DoesNotContain("{{", text);
				foreach (string value in values) Assert.Contains(value, text);
			}
			string snappy = File.ReadAllText(Path.Combine(target, "system", "snappyHexMeshDict"));
			Assert.Contains("gasDomain_walls { nSurfaceLayers 3; }", snappy);
			Assert.DoesNotContain("inlet_test { nSurfaceLayers", snappy);
			Assert.DoesNotContain("outlet_test { nSurfaceLayers", snappy);
		}
		finally { Directory.Delete(directory, true); }
	}
}
