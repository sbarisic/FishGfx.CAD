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

public enum CfdAnalysisMode
{
	Steady,
	EngineTransient,
}

public enum TransientInitialisationMode
{
	Uniform,
	CompatibleSteadyResult,
}

public sealed record CfdCylinderAssignment(
	int CylinderNumber,
	string ComponentId);

public sealed record CfdEngineTransientSettings
{
	public const string CorsaPresetId = "corsa-3500";
	public const string PulsePresetId = "estimated-exhaust-v1";
	public const string BoundaryModelLabel =
		"Prescribed-flow inlets, fixed-static-pressure outlet, no turbine impedance model.";
	public const int PulseGeneratorVersion = 2;
	public const int PeriodicityAlgorithmVersion = 1;
	public double EngineDisplacementCc { get; init; } = 1364;
	public double EngineSpeedRpm { get; init; } = 3500;
	public int[] FiringOrder { get; init; } = [1, 3, 4, 2];
	public List<CfdCylinderAssignment> CylinderAssignments { get; init; } = [];
	public string PulsePreset { get; init; } = PulsePresetId;
	public double EventStartDegreesAfterFiring { get; init; } = 120;
	public double EventEndDegreesAfterFiring { get; init; } = 380;
	public double PulseTableStepDegrees { get; init; } = 0.25;
	public double SolverAlignmentDegrees { get; init; } = 2;
	public double MaximumTimeStepDegrees { get; init; } = 0.25;
	public double MinimumTimeStepDegrees { get; init; } = 0.00001;
	public int CollapsedTimeStepPollLimit { get; init; } = 10;
	public double MaximumCourantNumber { get; init; } = 0.5;
	public int StartupRampCycles { get; init; } = 1;
	public int MinimumCycles { get; init; } = 3;
	public int MaximumCycles { get; init; } = 6;
	public double FlowWaveformNrmseTolerance { get; init; } = 0.01;
	public double FlowWaveformScaleFloorKgPerSecond { get; init; } = 1e-4;
	public double PressureWaveformNrmseTolerance { get; init; } = 0.01;
	public double PressureWaveformScaleFloorPa { get; init; } = 100;
	public double MeanPressureAbsoluteTolerancePa { get; init; } = 5;
	public double MeanPressureRelativeTolerance { get; init; } = 0.00005;
	public double DomainMassRelativeTolerance { get; init; } = 0.001;
	public double DomainMassFloorKg { get; init; } = 1e-9;
	public TransientInitialisationMode InitialisationMode { get; init; }
	public string? InitialSteadySolveHash { get; init; }
	public Guid? InitialSteadyCaseId { get; init; }

	[JsonIgnore]
	public double SecondsPerDegree => 60.0 / (EngineSpeedRpm * 360.0);

	[JsonIgnore]
	public double CycleDurationSeconds => SecondsPerDegree * 720.0;

	public void Validate()
	{
		double[] positive =
		{
			EngineDisplacementCc,
			EngineSpeedRpm,
			PulseTableStepDegrees,
			SolverAlignmentDegrees,
			MaximumTimeStepDegrees,
			MinimumTimeStepDegrees,
			MaximumCourantNumber,
			FlowWaveformNrmseTolerance,
			FlowWaveformScaleFloorKgPerSecond,
			PressureWaveformNrmseTolerance,
			PressureWaveformScaleFloorPa,
			MeanPressureAbsoluteTolerancePa,
			MeanPressureRelativeTolerance,
			DomainMassRelativeTolerance,
			DomainMassFloorKg,
		};
		if (positive.Any(value => !double.IsFinite(value) || value <= 0)
			|| !double.IsFinite(EventStartDegreesAfterFiring)
			|| !double.IsFinite(EventEndDegreesAfterFiring)
			|| EventStartDegreesAfterFiring < 0
			|| EventEndDegreesAfterFiring <= EventStartDegreesAfterFiring
			|| EventEndDegreesAfterFiring > 720
			|| MinimumCycles < 2
			|| MaximumCycles < MinimumCycles
			|| MaximumTimeStepDegrees > SolverAlignmentDegrees
			|| MinimumTimeStepDegrees >= MaximumTimeStepDegrees
			|| CollapsedTimeStepPollLimit < 1
			|| StartupRampCycles < 1
			|| !DividesCycle(PulseTableStepDegrees)
			|| !DividesCycle(SolverAlignmentDegrees)
			|| string.IsNullOrWhiteSpace(PulsePreset))
		{
			throw new InvalidDataException("The engine-transient settings are invalid.");
		}
		if (StartupRampCycles >= MinimumCycles)
			throw new InvalidDataException("The startup ramp must finish before periodicity comparisons begin.");
		int[] cylinders = CylinderAssignments.Select(value => value.CylinderNumber).Order().ToArray();
		if (cylinders.Length == 0
			|| cylinders.Distinct().Count() != cylinders.Length
			|| CylinderAssignments.Any(value => string.IsNullOrWhiteSpace(value.ComponentId))
			|| CylinderAssignments.Select(value => value.ComponentId).Distinct(StringComparer.Ordinal).Count() != cylinders.Length
			|| FiringOrder.Length != cylinders.Length
			|| !FiringOrder.Order().SequenceEqual(cylinders))
		{
			throw new InvalidDataException("The firing order must be a permutation of one-to-one cylinder assignments.");
		}
		if (InitialisationMode == TransientInitialisationMode.CompatibleSteadyResult
			&& (string.IsNullOrWhiteSpace(InitialSteadySolveHash) || InitialSteadyCaseId is null))
		{
			throw new InvalidDataException("Compatible steady initialization requires a steady case ID and SolveHash.");
		}
	}

	public void ValidateAgainst(GasPathManifest path)
	{
		Validate();
		string[] inletIds = path.Openings.Where(value => value.Role == "inlet")
			.Select(value => value.ComponentId).Order(StringComparer.Ordinal).ToArray();
		string[] assigned = CylinderAssignments.Select(value => value.ComponentId)
			.Order(StringComparer.Ordinal).ToArray();
		if (!assigned.SequenceEqual(inletIds, StringComparer.Ordinal))
		{
			throw new InvalidDataException("Every selected gas-path inlet must be assigned to exactly one cylinder.");
		}
	}

	private static bool DividesCycle(double angle)
	{
		double count = 720.0 / angle;
		return double.IsFinite(count) && Math.Abs(count - Math.Round(count)) <= 1e-9;
	}
}

public sealed record CfdCaptureSettings
{
	public const int CaptureVersion = 1;
	public double RetainedOutputAngleDegrees { get; init; } = 2;
	public string[] Fields { get; init; } = ["p", "T", "U", "rho", "Ma", "yPlus"];
	public double MinimumMetricMassFlowKgPerSecond { get; init; } = 1e-5;

	public void Validate(CfdEngineTransientSettings transient)
	{
		if (!double.IsFinite(RetainedOutputAngleDegrees) || RetainedOutputAngleDegrees <= 0
			|| Math.Abs(720.0 / RetainedOutputAngleDegrees - Math.Round(720.0 / RetainedOutputAngleDegrees)) > 1e-9
			|| RetainedOutputAngleDegrees < transient.SolverAlignmentDegrees
			|| Math.Abs(RetainedOutputAngleDegrees / transient.SolverAlignmentDegrees
				- Math.Round(RetainedOutputAngleDegrees / transient.SolverAlignmentDegrees)) > 1e-9
			|| !double.IsFinite(MinimumMetricMassFlowKgPerSecond)
			|| MinimumMetricMassFlowKgPerSecond <= 0
			|| Fields.Length == 0
			|| Fields.Any(string.IsNullOrWhiteSpace))
		{
			throw new InvalidDataException("The transient capture settings are invalid.");
		}
	}
}

public sealed record CfdResultStorageSettings
{
	public const int FormatVersion = 1;
	public const int SamplingPolicyVersion = 1;
	public const int CompressionVersion = 1;
	public int MaximumVolumeSamples { get; init; } = 80_000;
	public int CompressionQuality { get; init; } = 5;

	public void Validate()
	{
		if (MaximumVolumeSamples < 1 || CompressionQuality is < 0 or > 11)
			throw new InvalidDataException("The transient result-storage settings are invalid.");
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
	PeriodicConverged,
	MaximumCyclesWithoutPeriodicity,
	TimeStepCollapse,
	FatalError,
	Cancelled,
}

public sealed record CfdPeriodicityResult
{
	public bool Passed { get; init; }
	public int ComparedCycle { get; init; }
	public double FlowWaveformNrmse { get; init; }
	public double FlowWaveformTolerance { get; init; }
	public double PressureFluctuationNrmse { get; init; }
	public double PressureFluctuationTolerance { get; init; }
	public double MeanPressureChangePa { get; init; }
	public double MeanPressureTolerancePa { get; init; }
	public double DomainMassRelativeError { get; init; }
	public double DomainMassRelativeTolerance { get; init; }
}

public enum CfdTransientMetricState
{
	Valid,
	Unavailable,
	ReverseFlow,
}

public sealed record CfdTransientFrameMetric
{
	public int FrameIndex { get; init; }
	public double TimeSeconds { get; init; }
	public double CrankAngleDegrees { get; init; }
	public CfdTransientMetricState State { get; init; }
	public double NetInletMassFlowKgPerSecond { get; init; }
	public double NetOutletMassFlowKgPerSecond { get; init; }
	public double? PressureLossPa { get; init; }
	public double LocalInletBackflowFraction { get; init; }
	public double OutletBackflowFraction { get; init; }
	public int NominallyClosedInletCount { get; init; }
	public double NominallyClosedReverseFlowAreaFraction { get; init; }
	public double NominallyClosedTangentialVelocityAreaWeightedMeanMps { get; init; }
}

public sealed record CfdTransientResultReference(
	string RelativePath,
	string Sha256,
	int AcceptedCycle,
	int FrameCount,
	CfdPeriodicityResult Periodicity);

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

public sealed record CfdTransientResultSummary
{
	public CfdRunStatus Status { get; init; }
	public string ModelLabel { get; init; } = CfdEngineTransientSettings.BoundaryModelLabel;
	public double? CycleAveragePressureLossPa { get; init; }
	public double CycleMassImbalanceFraction { get; init; }
	public IReadOnlyList<CfdTransientFrameMetric> Frames { get; init; } = [];
	public string? Diagnostic { get; init; }
}

public sealed record CfdCaseResults
{
	public CfdResultSummary? Steady { get; init; }
	public CfdTransientResultReference? Transient { get; init; }
	public CfdTransientResultSummary? TransientSummary { get; init; }
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
	public const int CurrentVersion = 2;
	public string Schema { get; init; } = SchemaName;
	public int Version { get; init; } = CurrentVersion;
	public Guid CaseId { get; init; } = Guid.NewGuid();
	public string SourcePackagePath { get; init; } = string.Empty;
	public string PackageFileHash { get; init; } = string.Empty;
	public string SourceHash { get; init; } = string.Empty;
	public string SelectedGasPathId { get; init; } = string.Empty;
	public Dictionary<string, string> ManualClassificationOverrides { get; init; } = new(StringComparer.Ordinal);
	public List<CfdPatchMatchDiagnostic> MatchingDiagnostics { get; init; } = [];
	public CfdAnalysisMode AnalysisMode { get; init; }
	public CfdMeshSettings Mesh { get; init; } = new();
	public CfdSolverSettings Solver { get; init; } = new();
	public CfdEngineTransientSettings? EngineTransient { get; init; }
	public CfdCaptureSettings Capture { get; init; } = new();
	public CfdResultStorageSettings ResultStorage { get; init; } = new();
	public CfdToolchainFingerprint? Toolchain { get; init; }
	public string? MeshHash { get; init; }
	public string? SolveHash { get; init; }
	public string? CaptureHash { get; init; }
	public string? ResultHash { get; init; }
	public CfdCaseResults Results { get; init; } = new();

	public void Validate()
	{
		Mesh.Validate();
		Solver.Validate();
		ResultStorage.Validate();
		if (AnalysisMode == CfdAnalysisMode.EngineTransient)
		{
			if (EngineTransient is null)
				throw new InvalidDataException("An engine-transient case requires transient settings.");
			EngineTransient.Validate();
			Capture.Validate(EngineTransient);
		}
		else if (EngineTransient is not null)
		{
			throw new InvalidDataException("A steady case cannot contain engine-transient settings.");
		}
	}
}
