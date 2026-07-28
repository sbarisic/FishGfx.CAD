using System.Globalization;
using System.Text;

namespace FishGfx.CFD;

public readonly record struct CfdPoint3(double X, double Y, double Z);

public sealed record CfdPreparedGeometry(
	string MultiRegionStlPath,
	CfdPoint3 MinimumMeters,
	CfdPoint3 MaximumMeters,
	CfdPoint3 InteriorPointMeters,
	double SmallestInletHydraulicDiameterMm);

public static class OpenFoamCaseGenerator
{
	public const string TemplateVersion = "openfoam14-steady-compressible-7";
	public const int PostProcessingVersion = 1;

	public static void Generate(
		string caseDirectory,
		CfdCaseDocument document,
		LoadedGasPackage package,
		CfdPreparedGeometry geometry)
	{
		document.Mesh.Validate();
		document.Solver.Validate();
		GasPathManifest path = package.Manifest.Paths.Single(item => item.Id == document.SelectedGasPathId);
		string templateRoot = Path.Combine(
			AppContext.BaseDirectory,
			"Templates",
			"OpenFoam14",
			"SteadyCompressible");
		if (!Directory.Exists(templateRoot))
		{
			throw new DirectoryNotFoundException($"OpenFOAM template not found: {templateRoot}");
		}
		Directory.CreateDirectory(caseDirectory);
		foreach (string source in Directory.EnumerateFiles(templateRoot, "*", SearchOption.AllDirectories))
		{
			string relative = Path.GetRelativePath(templateRoot, source);
			string destination = Path.Combine(caseDirectory, relative);
			Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
			string content = File.ReadAllText(source);
			File.WriteAllText(destination, Substitute(content, document, path, geometry), new UTF8Encoding(false));
		}
		string triSurface = Path.Combine(caseDirectory, "constant", "triSurface");
		Directory.CreateDirectory(triSurface);
		File.Copy(geometry.MultiRegionStlPath, Path.Combine(triSurface, "gas-domain.stl"), true);
	}

	private static string Substitute(
		string content,
		CfdCaseDocument document,
		GasPathManifest path,
		CfdPreparedGeometry geometry)
	{
		CfdMeshSettings mesh = document.Mesh;
		CfdSolverSettings solver = document.Solver;
		CfdFluidPreset fluid = solver.Fluid;
		double cellSize = geometry.SmallestInletHydraulicDiameterMm / 1000.0
			/ mesh.CellsAcrossSmallestInlet;
		(CfdPoint3 minimum, CfdPoint3 maximum, int nx, int ny, int nz) =
			BackgroundMesh(geometry, mesh, cellSize);
		double targetBaseCells = Math.Max(1, mesh.MaximumCells / 8.0);
		double baseCells = (double)nx * ny * nz;
		if (baseCells > targetBaseCells)
		{
			cellSize *= Math.Cbrt(baseCells / targetBaseCells);
			(minimum, maximum, nx, ny, nz) = BackgroundMesh(geometry, mesh, cellSize);
		}
		Dictionary<string, string> values = new(StringComparer.Ordinal)
		{
			["MAX_ITERATIONS"] = I(solver.MaximumIterations),
			["OUTLET_PRESSURE"] = F(solver.OutletPressurePa),
			["INLET_TEMPERATURE"] = F(solver.InletTemperatureK),
			["MOLECULAR_WEIGHT"] = F(fluid.MolecularWeight),
			["CP"] = F(fluid.SpecificHeatCp),
			["MU"] = F(fluid.DynamicViscosity),
			["PR"] = F(fluid.PrandtlNumber),
			["PRT"] = F(fluid.TurbulentPrandtlNumber),
			["MIN_X"] = F(minimum.X), ["MIN_Y"] = F(minimum.Y), ["MIN_Z"] = F(minimum.Z),
			["MAX_X"] = F(maximum.X), ["MAX_Y"] = F(maximum.Y), ["MAX_Z"] = F(maximum.Z),
			["NX"] = I(nx), ["NY"] = I(ny), ["NZ"] = I(nz),
			["LOCATION_X"] = F(geometry.InteriorPointMeters.X),
			["LOCATION_Y"] = F(geometry.InteriorPointMeters.Y),
			["LOCATION_Z"] = F(geometry.InteriorPointMeters.Z),
			["MAX_CELLS"] = I(mesh.MaximumCells),
			["FEATURE_ANGLE"] = F(mesh.FeatureAngleDegrees),
			["INCLUDED_ANGLE"] = F(180.0 - mesh.FeatureAngleDegrees),
			["WALL_LEVEL"] = I(mesh.WallRefinementLevel),
			["OPENING_LEVEL"] = I(mesh.OpeningRefinementLevel),
			["LAYER_COUNT"] = I(mesh.LayerCount),
			["LAYER_EXPANSION"] = F(mesh.LayerExpansionRatio),
			["FIRST_LAYER_METERS"] = F(mesh.FirstLayerThicknessMm / 1000.0),
			["PATCH_REGIONS"] = PatchRegions(path, mesh),
			["LAYERS"] = mesh.LayerCount == 0 ? string.Empty : $"{MeshPatchName("walls")} {{ nSurfaceLayers {mesh.LayerCount}; }}",
			["U_BOUNDARIES"] = Boundaries(path, document, "U"),
			["P_BOUNDARIES"] = Boundaries(path, document, "p"),
			["T_BOUNDARIES"] = Boundaries(path, document, "T"),
			["K_BOUNDARIES"] = Boundaries(path, document, "k"),
			["OMEGA_BOUNDARIES"] = Boundaries(path, document, "omega"),
			["NUT_BOUNDARIES"] = Boundaries(path, document, "nut"),
			["ALPHAT_BOUNDARIES"] = Boundaries(path, document, "alphat"),
		};
		foreach ((string key, string value) in values)
		{
			content = content.Replace("{{" + key + "}}", value, StringComparison.Ordinal);
		}
		if (content.Contains("{{", StringComparison.Ordinal))
		{
			throw new InvalidDataException("An OpenFOAM template token was not substituted.");
		}
		return content;
	}

	private static (CfdPoint3 Minimum, CfdPoint3 Maximum, int Nx, int Ny, int Nz)
		BackgroundMesh(CfdPreparedGeometry geometry, CfdMeshSettings mesh, double cellSize)
	{
		CfdPoint3 minimum = new(
			geometry.MinimumMeters.X - cellSize * mesh.BoundsMarginCells,
			geometry.MinimumMeters.Y - cellSize * mesh.BoundsMarginCells,
			geometry.MinimumMeters.Z - cellSize * mesh.BoundsMarginCells);
		CfdPoint3 maximum = new(
			geometry.MaximumMeters.X + cellSize * mesh.BoundsMarginCells,
			geometry.MaximumMeters.Y + cellSize * mesh.BoundsMarginCells,
			geometry.MaximumMeters.Z + cellSize * mesh.BoundsMarginCells);
		int nx = Math.Max(1, (int)Math.Ceiling((maximum.X - minimum.X) / cellSize));
		int ny = Math.Max(1, (int)Math.Ceiling((maximum.Y - minimum.Y) / cellSize));
		int nz = Math.Max(1, (int)Math.Ceiling((maximum.Z - minimum.Z) / cellSize));
		return (minimum, maximum, nx, ny, nz);
	}

	private static string PatchRegions(GasPathManifest path, CfdMeshSettings mesh)
	{
		StringBuilder result = new();
		result.AppendLine($"walls {{ level ({mesh.WallRefinementLevel} {mesh.WallRefinementLevel}); patchInfo {{ type wall; }} }}");
		foreach (GasOpeningManifest opening in path.Openings)
		{
			result.AppendLine(
				$"{opening.PatchName} {{ level ({mesh.OpeningRefinementLevel} {mesh.OpeningRefinementLevel}); patchInfo {{ type patch; }} }}");
		}
		return result.ToString();
	}

	private static string Boundaries(GasPathManifest path, CfdCaseDocument document, string field)
	{
		StringBuilder result = new();
		result.Append(MeshPatchName("walls")).Append("\n{\n")
			.Append(WallBoundary(field, document)).Append("}\n");
		GasOpeningManifest[] inlets = path.Openings.Where(item => item.Role == "inlet").ToArray();
		double equalFlow = document.Solver.TotalMassFlowKgPerSecond / inlets.Length;
		foreach (GasOpeningManifest inlet in inlets)
		{
			double massFlow = document.Solver.RunnerMassFlows.TryGetValue(inlet.ComponentId, out double configured)
				? configured : equalFlow;
			result.Append(MeshPatchName(inlet.PatchName)).Append("\n{\n")
				.Append(InletBoundary(field, document, inlet, massFlow)).Append("}\n");
		}
		GasOpeningManifest outlet = path.Openings.Single(item => item.Role == "outlet");
		result.Append(MeshPatchName(outlet.PatchName)).Append("\n{\n")
			.Append(OutletBoundary(field, document)).Append("}\n");
		return result.ToString();
	}

	internal static string MeshPatchName(string regionName) => "gasDomain_" + regionName;

	private static string WallBoundary(string field, CfdCaseDocument document) => field switch
	{
		"U" => "    type noSlip;\n",
		"p" or "T" => "    type zeroGradient;\n",
		"k" => "    type kqRWallFunction;\n    value uniform 0.1;\n",
		"omega" => "    type omegaWallFunction;\n    value uniform 100;\n",
		"nut" => "    type nutkWallFunction;\n    value uniform 0;\n",
		"alphat" => $"    type compressible::alphatWallFunction;\n    Prt {F(document.Solver.Fluid.TurbulentPrandtlNumber)};\n    value uniform 0;\n",
		_ => throw new ArgumentOutOfRangeException(nameof(field)),
	};

	private static string InletBoundary(
		string field,
		CfdCaseDocument document,
		GasOpeningManifest inlet,
		double massFlow)
	{
		double areaM2 = inlet.Fingerprint.Area * 1e-6;
		double rhoGuess = document.Solver.OutletPressurePa
			/ (document.Solver.Fluid.SpecificGasConstant * document.Solver.InletTemperatureK);
		double speed = massFlow / (rhoGuess * areaM2);
		double hydraulicDiameterM = 2 * Math.Sqrt(areaM2 / Math.PI);
		double k = 1.5 * Math.Pow(document.Solver.TurbulenceIntensity * speed, 2);
		double length = document.Solver.MixingLengthFraction * hydraulicDiameterM;
		double omega = Math.Sqrt(k) / (Math.Pow(0.09, 0.25) * length);
		return field switch
		{
			"U" => $"    type flowRateInletVelocity;\n    massFlowRate constant {F(massFlow)};\n    rhoInlet {F(rhoGuess)};\n    value uniform (0 0 0);\n",
			"p" => "    type zeroGradient;\n",
			"T" => $"    type fixedValue;\n    value uniform {F(document.Solver.InletTemperatureK)};\n",
			"k" => $"    type fixedValue;\n    value uniform {F(k)};\n",
			"omega" => $"    type fixedValue;\n    value uniform {F(omega)};\n",
			"nut" or "alphat" => "    type calculated;\n    value uniform 0;\n",
			_ => throw new ArgumentOutOfRangeException(nameof(field)),
		};
	}

	private static string OutletBoundary(string field, CfdCaseDocument document) => field switch
	{
		"U" => "    type pressureInletOutletVelocity;\n    value uniform (0 0 0);\n",
		"p" => $"    type fixedValue;\n    value uniform {F(document.Solver.OutletPressurePa)};\n",
		"T" => $"    type inletOutlet;\n    inletValue uniform {F(document.Solver.InletTemperatureK)};\n    value uniform {F(document.Solver.InletTemperatureK)};\n",
		"k" => "    type inletOutlet;\n    inletValue uniform 0.1;\n    value uniform 0.1;\n",
		"omega" => "    type inletOutlet;\n    inletValue uniform 100;\n    value uniform 100;\n",
		"nut" or "alphat" => "    type calculated;\n    value uniform 0;\n",
		_ => throw new ArgumentOutOfRangeException(nameof(field)),
	};

	private static string F(double value) => value.ToString("R", CultureInfo.InvariantCulture);
	private static string I(int value) => value.ToString(CultureInfo.InvariantCulture);
}
