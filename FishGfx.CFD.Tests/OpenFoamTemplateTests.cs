using FishGfx.Cad;
using FishGfx.CFD;
using Xunit;

namespace FishGfx.CFD.Tests;

public sealed class OpenFoamTemplateTests
{
	[Fact]
	public void GeneratesTransientBackwardTemplateAndDirectionAwareInlets()
	{
		string directory = Path.Combine(Path.GetTempPath(), $"fishgfx-transient-foam-{Guid.NewGuid():N}");
		Directory.CreateDirectory(directory);
		try
		{
			string stl = Path.Combine(directory, "source.stl");
			File.WriteAllText(stl, "solid walls\nendsolid walls\n");
			GasOpeningFingerprint fingerprint = new(1000, [0, 0, 0], [0, 0, 1], 1, 100, [], [], "plane");
			GasOpeningManifest[] openings = Enumerable.Range(1, 4)
				.Select(index => new GasOpeningManifest($"start{index}", $"inlet_{index}", "inlet", $"runner{index}", fingerprint))
				.Append(new("end", "outlet", "outlet", "collector", fingerprint)).ToArray();
			GasPathManifest path = new("collector", "collector", "Collector", "component", openings);
			GasPackageManifest manifest = new("fishgfx.gas-patches", 1, "mm", new string('0', 64),
				CadPatchMatchingPolicy.Version1, [path]);
			CfdEngineTransientSettings transient = new()
			{
				CylinderAssignments = Enumerable.Range(1, 4)
					.Select(index => new CfdCylinderAssignment(index, $"runner{index}")).ToList(),
			};
			CfdCaseDocument document = new()
			{
				SelectedGasPathId = path.Id,
				AnalysisMode = CfdAnalysisMode.EngineTransient,
				EngineTransient = transient,
			};
			string target = Path.Combine(directory, "case");
			OpenFoamCaseGenerator.Generate(target, document,
				new("fixture", "", "", [], [], manifest),
				new(stl, new(-.1, -.1, -.1), new(.1, .1, .1), new(0, 0, 0), 40));
			string schemes = File.ReadAllText(Path.Combine(target, "system", "fvSchemes"));
			Assert.Contains("default backward", schemes);
			Assert.Contains("div(phi,U) bounded Gauss upwind", schemes);
			Assert.Contains("Gauss linear limited 0.5", schemes);
			string solution = File.ReadAllText(Path.Combine(target, "system", "fvSolution"));
			Assert.Contains("solver PCG", solution);
			Assert.Contains("preconditioner DIC", solution);
			Assert.Contains("nNonOrthogonalCorrectors 1", solution);
			string constraints = File.ReadAllText(Path.Combine(target, "system", "fvConstraints"));
			Assert.Contains("type            limitMag", constraints);
			Assert.Contains("max             400", constraints);
			Assert.DoesNotContain("type            limitPressure", constraints);
			string control = File.ReadAllText(Path.Combine(target, "system", "controlDict"));
			Assert.Contains("adjustTimeStep yes", control);
			Assert.Contains("writeControl adjustableRunTime", control);
			Assert.Contains("outletMassFlow", control);
			Assert.Contains("operation sum", control);
			Assert.Contains("outletPressure", control);
			Assert.Contains("operation areaAverage", control);
			Assert.Contains("domainMass", control);
			Assert.Contains("operation volIntegrate", control);
			Assert.Contains("purgeWrite 362", control);
			string velocity = File.ReadAllText(Path.Combine(target, "0", "U"));
			Assert.Contains("type table", velocity);
			Assert.Contains("massFlowRate", velocity);
			Assert.Contains("type inletOutlet", File.ReadAllText(Path.Combine(target, "0", "T")));
			string kineticEnergy = File.ReadAllText(Path.Combine(target, "0", "k"));
			Assert.Contains("type inletOutlet", kineticEnergy);
			Assert.Contains("inletValue uniform", kineticEnergy);
			string omega = File.ReadAllText(Path.Combine(target, "0", "omega"));
			Assert.Contains("type inletOutlet", omega);
			Assert.Contains("inletValue uniform", omega);
			string pressure = File.ReadAllText(Path.Combine(target, "0", "p"));
			Assert.Contains("type waveTransmissive", pressure);
			Assert.Contains("fieldInf 101325", pressure);
			Assert.Contains("lInf 0.01", pressure);
		}
		finally { Directory.Delete(directory, true); }
	}

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
