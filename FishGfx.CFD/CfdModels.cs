using System.Text.Json.Serialization;

namespace FishGfx.CFD;

public sealed record CfdFluidPreset(
	string Id,
	double SpecificGasConstant,
	double SpecificHeatCp,
	double Gamma,
	double DynamicViscosity,
	double PrandtlNumber,
	double TurbulentPrandtlNumber)
{
	public static CfdFluidPreset IdealAirExhaustV1 { get; } = new(
		"ideal-air-exhaust-v1",
		287.05,
		1005.0,
		1.4,
		1.82e-5,
		0.71,
		0.85);

	public void Validate()
	{
		double[] values =
		{
			SpecificGasConstant,
			SpecificHeatCp,
			Gamma,
			DynamicViscosity,
			PrandtlNumber,
			TurbulentPrandtlNumber,
		};
		if (string.IsNullOrWhiteSpace(Id) || values.Any(value => !double.IsFinite(value) || value <= 0))
		{
			throw new InvalidDataException("The CFD fluid preset must have an ID and finite positive properties.");
		}
		if (SpecificHeatCp <= SpecificGasConstant || Gamma <= 1)
		{
			throw new InvalidDataException("The CFD fluid preset is thermodynamically invalid.");
		}
		double derivedGamma = SpecificHeatCp / (SpecificHeatCp - SpecificGasConstant);
		if (Math.Abs(derivedGamma - Gamma) > 0.01)
		{
			throw new InvalidDataException(
				$"Fluid gamma {Gamma:R} is inconsistent with Cp and R (derived {derivedGamma:R}).");
		}
	}

	[JsonIgnore]
	public double MolecularWeight => 8314.46261815324 / SpecificGasConstant;
}

public sealed record CfdMeshSettings
{
	public int CellsAcrossSmallestInlet { get; init; } = 18;
	public int BoundsMarginCells { get; init; } = 2;
	public int WallRefinementLevel { get; init; } = 1;
	public int OpeningRefinementLevel { get; init; } = 2;
	public int LayerCount { get; init; } = 3;
	public double LayerExpansionRatio { get; init; } = 1.2;
	public double FirstLayerThicknessMm { get; init; } = 0.15;
	public double FeatureAngleDegrees { get; init; } = 45;
	public int MaximumCells { get; init; } = 2_000_000;
	public const int SettingsVersion = 3;

	public static double DefaultFirstLayerThickness(double hydraulicDiameterMm)
	{
		if (!double.IsFinite(hydraulicDiameterMm) || hydraulicDiameterMm <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(hydraulicDiameterMm));
		}
		return Math.Clamp(hydraulicDiameterMm * 0.005, 0.05, 0.50);
	}

	public void Validate()
	{
		if (CellsAcrossSmallestInlet < 4 || BoundsMarginCells < 1
			|| WallRefinementLevel < 0 || OpeningRefinementLevel < 0
			|| LayerCount < 0 || MaximumCells < 1000
			|| !double.IsFinite(LayerExpansionRatio) || LayerExpansionRatio < 1
			|| !double.IsFinite(FirstLayerThicknessMm) || FirstLayerThicknessMm <= 0
			|| !double.IsFinite(FeatureAngleDegrees) || FeatureAngleDegrees <= 0
			|| FeatureAngleDegrees >= 180)
		{
			throw new InvalidDataException("The CFD mesh settings are invalid.");
		}
	}
}

public sealed record CfdSolverSettings
{
	public double OutletPressurePa { get; init; } = 101325;
	public double InletTemperatureK { get; init; } = 900;
	public double TotalMassFlowKgPerSecond { get; init; } = 0.1;
	public Dictionary<string, double> RunnerMassFlows { get; init; } = new(StringComparer.Ordinal);
	public double TurbulenceIntensity { get; init; } = 0.05;
	public double MixingLengthFraction { get; init; } = 0.07;
	public int MaximumIterations { get; init; } = 1000;
	public bool RetainFailedRuntime { get; init; }
	public CfdFluidPreset Fluid { get; init; } = CfdFluidPreset.IdealAirExhaustV1;

	public void Validate()
	{
		Fluid.Validate();
		if (!double.IsFinite(OutletPressurePa) || OutletPressurePa <= 0
			|| !double.IsFinite(InletTemperatureK) || InletTemperatureK <= 0
			|| !double.IsFinite(TotalMassFlowKgPerSecond) || TotalMassFlowKgPerSecond <= 0
			|| !double.IsFinite(TurbulenceIntensity) || TurbulenceIntensity <= 0
			|| !double.IsFinite(MixingLengthFraction) || MixingLengthFraction <= 0
			|| MaximumIterations < 1
			|| RunnerMassFlows.Values.Any(value => !double.IsFinite(value) || value <= 0))
		{
			throw new InvalidDataException("The CFD solver settings are invalid.");
		}
	}
}

public sealed record CfdToolchainFingerprint(
	string Distribution,
	string FoamVersion,
	string ProjectVersion,
	string WmOptions,
	string EnvironmentScriptPath,
	string EnvironmentScriptSha256,
	string TemplateVersion,
	int SnappySettingsVersion,
	int MatchingPolicyVersion,
	int PostProcessingVersion);

public enum CfdRunStatus
{
	NotRun,
	Converged,
	MaximumIterations,
	FatalError,
	Cancelled,
}

public sealed record CfdPatchMatchDiagnostic(
	string OpeningId,
	string? SelectedCandidate,
	double? BestScore,
	double? SecondBestScore,
	IReadOnlyList<string> FailedTolerances);

public sealed record CfdResultSummary
{
	public CfdRunStatus Status { get; init; }
	public int Iterations { get; init; }
	public double? MassImbalanceFraction { get; init; }
	public double? PressureLossPa { get; init; }
	public double? BackflowFraction { get; init; }
	public double? YPlusMinimum { get; init; }
	public double? YPlusAreaWeightedMean { get; init; }
	public double? YPlusMaximum { get; init; }
	public double? WallAreaBelowTargetFraction { get; init; }
	public double? WallAreaAboveTargetFraction { get; init; }
	public double? DensityConsistencyMaximumRelativeError { get; init; }
	public IReadOnlyList<CfdResidualSample> Residuals { get; init; } = [];
	public string? Diagnostic { get; init; }
}

public sealed record CfdResidualSample(
	int Iteration,
	string Field,
	double InitialResidual,
	double FinalResidual,
	int SolverIterations);

public sealed record CfdCaseDocument
{
	public const string SchemaName = "fishgfx.cfd-case";
	public const int CurrentVersion = 1;
	public string Schema { get; init; } = SchemaName;
	public int Version { get; init; } = CurrentVersion;
	public Guid CaseId { get; init; } = Guid.NewGuid();
	public string SourcePackagePath { get; init; } = string.Empty;
	public string PackageFileHash { get; init; } = string.Empty;
	public string SourceHash { get; init; } = string.Empty;
	public string SelectedGasPathId { get; init; } = string.Empty;
	public Dictionary<string, string> ManualClassificationOverrides { get; init; } = new(StringComparer.Ordinal);
	public List<CfdPatchMatchDiagnostic> MatchingDiagnostics { get; init; } = [];
	public CfdMeshSettings Mesh { get; init; } = new();
	public CfdSolverSettings Solver { get; init; } = new();
	public CfdToolchainFingerprint? Toolchain { get; init; }
	public string? MeshHash { get; init; }
	public string? SolveHash { get; init; }
	public CfdResultSummary? Results { get; init; }
}
