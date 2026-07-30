using FishGfx.Cad;
using FishGfx.CFD;
using Xunit;

namespace FishGfx.CFD.Tests;

public sealed class OpenFoamTemplateTests
{
	[Fact]
	public void GeneratesAmdGpuPetscSolversForEveryEligibleField()
	{
		string directory = Path.Combine(Path.GetTempPath(), $"fishgfx-gpu-foam-{Guid.NewGuid():N}");
		Directory.CreateDirectory(directory);
		try
		{
			string stl = Path.Combine(directory, "source.stl");
			File.WriteAllText(stl, "solid walls\nendsolid walls\n");
			GasOpeningFingerprint fingerprint = new(1000, [0, 0, 0], [0, 0, 1], 1, 100, [], [], "plane");
			GasPathManifest path = new("runner", "runner", "Runner", "component",
			[
				new("start", "inlet", "inlet", "runner", fingerprint),
				new("end", "outlet", "outlet", "runner", fingerprint),
			]);
			GasPackageManifest manifest = new("fishgfx.gas-patches", 1, "mm", new string('0', 64),
				CadPatchMatchingPolicy.Version1, [path]);
			CfdCaseDocument document = new() { SelectedGasPathId = path.Id };
			string target = Path.Combine(directory, "case");
			OpenFoamCaseGenerator.Generate(target, document,
				new("fixture", "", "", [], [], manifest),
				new(stl, new(-.1, -.1, -.1), new(.1, .1, .1), new(0, 0, 0), 40));

			string control = File.ReadAllText(Path.Combine(target, "system", "controlDict"));
			Assert.Contains("libs (\"libpetscFoam.so\")", control);
			string solution = File.ReadAllText(Path.Combine(target, "system", "fvSolution"));
			Assert.Contains("solver petsc", solution);
			Assert.Contains("pc_hypre_type \"boomeramg\"", solution);
			Assert.Contains("ksp_type \"bcgs\"", solution);
			Assert.Contains("vec_type \"hip\"", solution);
			Assert.Contains("mat_type \"aijhipsparse\"", solution);
			Assert.Contains("\"(rho|U|k|omega|e|h)\"", solution);
			Assert.Contains("matrix auto", solution);
			Assert.Contains("preconditioner auto", solution);
			string options = File.ReadAllText(Path.Combine(target, "system", "petscOptions"));
			Assert.Contains("-device_select 0", options);
			Assert.Contains("-log_view", options);
		}
		finally { Directory.Delete(directory, true); }
	}

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
				Compute = CfdComputeSettings.For(CfdComputeBackend.CpuNative),
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
			Assert.Contains("nOuterCorrectors 2", solution);
			Assert.Contains("nCorrectors 2", solution);
			Assert.Contains("nNonOrthogonalCorrectors 1", solution);
			Assert.Contains("p               0.5", solution);
			Assert.Contains("U               0.7", solution);
			Assert.Contains("h               0.7", solution);
			Assert.Contains("k               0.7", solution);
			Assert.Contains("omega           0.7", solution);
			string constraints = File.ReadAllText(Path.Combine(target, "system", "fvConstraints"));
			Assert.Contains("type            limitMag", constraints);
			Assert.Contains("max             400", constraints);
			Assert.Contains("field           p", constraints);
			Assert.Contains("min             1000", constraints);
			Assert.Contains("max             5000000", constraints);
			Assert.Contains("field           rho", constraints);
			Assert.Contains("min             0.001", constraints);
			Assert.Contains("max             50", constraints);
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

	[Fact]
	public void GeneratesTurbineFanPressureWithStrictMapLimit()
	{
		string directory = Path.Combine(Path.GetTempPath(), $"fishgfx-turbine-foam-{Guid.NewGuid():N}");
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
			CfdCaseDocument document = new()
			{
				SelectedGasPathId = path.Id,
				AnalysisMode = CfdAnalysisMode.EngineTransient,
				EngineTransient = new CfdEngineTransientSettings
				{
					CylinderAssignments = Enumerable.Range(1, 4)
						.Select(index => new CfdCylinderAssignment(index, $"runner{index}")).ToList(),
				},
				OperatingPoint = new CfdEngineOperatingPoint(),
				Solver = new CfdSolverSettings
				{
					TotalMassFlowKgPerSecond = new CfdEngineOperatingPoint().ExhaustMassFlowKgPerSecond,
				},
				TurbineBoundary = new CfdTurbineBoundarySettings
				{
					Mode = CfdOutletBoundaryMode.TurbineMapImpedance,
					PresetId = CfdTurbineBoundarySettings.GarrettG25550PresetId,
					WastegateClosed = true,
				},
				Mesh = CfdMeshQualityPresets.Corsa(CfdMeshQuality.Preview),
			};
			string target = Path.Combine(directory, "case");
			OpenFoamCaseGenerator.Generate(target, document,
				new("fixture", "", "", [], [], manifest),
				new(stl, new(-.1, -.1, -.1), new(.1, .1, .1), new(0, 0, 0), 40));
			string pressure = File.ReadAllText(Path.Combine(target, "0", "p"));
			Assert.Contains("type waveTransmissive", pressure);
			Assert.Contains("fieldInf 13", pressure);
			Assert.Contains("rho rho", pressure);
			string[] csv = File.ReadAllLines(Path.Combine(target, "constant", "turbinePressureVsQ.csv"));
			Assert.True(csv.Length > 4);
			Assert.Equal("Q_m3_per_s,fanCurve_Pa", csv[0]);
			Assert.Equal("0,0", csv[1]);
			string control = File.ReadAllText(Path.Combine(target, "system", "controlDict"));
			Assert.Contains("inletPhiSum0", control);
			Assert.Contains("inletPhiMax3", control);
			string constraints = File.ReadAllText(Path.Combine(target, "system", "fvConstraints"));
			Assert.Contains("type            limitMag", constraints);
			Assert.Contains("field           U", constraints);
			Assert.Contains("field           p", constraints);
			Assert.Contains("field           rho", constraints);
			Assert.Contains("field           k", constraints);
			Assert.Contains("field           omega", constraints);
			Assert.Contains("type            limitTemperature", constraints);
			Assert.Contains("min             250", constraints);
			Assert.Contains("max             1800", constraints);
			string momentum = File.ReadAllText(Path.Combine(target, "constant", "momentumTransport"));
			Assert.Contains("simulationType laminar", momentum);

			string balancedTarget = Path.Combine(directory, "balanced-case");
			OpenFoamCaseGenerator.Generate(
				balancedTarget,
				document with { Mesh = CfdMeshQualityPresets.Corsa(CfdMeshQuality.Balanced) },
				new("fixture", "", "", [], [], manifest),
				new(stl, new(-.1, -.1, -.1), new(.1, .1, .1), new(0, 0, 0), 40));
			string balancedPressure = File.ReadAllText(Path.Combine(balancedTarget, "0", "p"));
			Assert.Contains("type fanPressure", balancedPressure);
			Assert.Contains("direction out", balancedPressure);
			Assert.Contains("outOfBounds clamp", balancedPressure);
			string balancedMomentum = File.ReadAllText(
				Path.Combine(balancedTarget, "constant", "momentumTransport"));
			Assert.Contains("simulationType RAS", balancedMomentum);
		}
		finally { Directory.Delete(directory, true); }
	}
}
