namespace FishGfx.CFD;

public sealed record CfdComputeBenchmarkRun(
	CfdComputeBackend Backend,
	int Repeat,
	CfdRunStatus Status,
	double FoamRunWallSeconds,
	int LinearSolveCount,
	int LinearIterations);

public sealed record CfdComputeEquivalence(
	bool Passed,
	double PressureRelativeL2,
	double TemperatureRelativeL2,
	double VelocityRelativeL2,
	double DensityRelativeL2,
	double OutletFlowWaveformNrmse,
	double OutletPressureWaveformNrmse,
	double IntegratedMetricRelativeError);

public sealed record CfdComputeBenchmarkReport(
	string Schema,
	int Version,
	DateTimeOffset CreatedUtc,
	string SourceCase,
	IReadOnlyList<CfdComputeBenchmarkRun> Runs,
	double CpuMedianSeconds,
	double GpuMedianSeconds,
	double Speedup,
	double RequiredSpeedup,
	CfdComputeEquivalence Equivalence,
	bool Passed);

public static class CfdComputeEquivalenceChecker
{
	public const double FieldL2Tolerance = 1e-4;
	public const double IntegratedMetricTolerance = 0.005;

	public static async Task<CfdComputeEquivalence> CompareAsync(
		string cpuCasePath,
		CfdCaseDocument cpu,
		string gpuCasePath,
		CfdCaseDocument gpu)
	{
		(double p, double t, double u, double rho, double flow, double pressure) fields = cpu.AnalysisMode switch
		{
			CfdAnalysisMode.Steady => SteadyComparison(cpuCasePath, cpu, gpuCasePath, gpu),
			CfdAnalysisMode.EngineTransient => await CompareTransient(cpuCasePath, cpu, gpuCasePath, gpu),
			_ => throw new ArgumentOutOfRangeException(),
		};
		double metric = IntegratedMetricError(cpu, gpu);
		bool passed = new[] { fields.p, fields.t, fields.u, fields.rho }.All(value => value <= FieldL2Tolerance)
			&& fields.flow <= 0.01 && fields.pressure <= 0.01
			&& metric <= IntegratedMetricTolerance;
		return new(passed, fields.p, fields.t, fields.u, fields.rho,
			fields.flow, fields.pressure, metric);
	}

	private static (double p, double t, double u, double rho, double flow, double pressure) SteadyComparison(
		string cpuCasePath, CfdCaseDocument cpu, string gpuCasePath, CfdCaseDocument gpu)
	{
		(double p, double t, double u, double rho) = CompareSteady(cpuCasePath, cpu, gpuCasePath, gpu);
		return (p, t, u, rho, 0, 0);
	}

	private static (double p, double t, double u, double rho) CompareSteady(
		string cpuCasePath,
		CfdCaseDocument cpu,
		string gpuCasePath,
		CfdCaseDocument gpu)
	{
		LoadedGasPackage package = LoadPackage(cpuCasePath, cpu);
		GasPathManifest path = package.Manifest.Paths.Single(value => value.Id == cpu.SelectedGasPathId);
		VerifiedOpenFoamResults left = OpenFoamResultVerifier.Verify(cpuCasePath + ".work/results", path);
		VerifiedOpenFoamResults right = OpenFoamResultVerifier.Verify(gpuCasePath + ".work/results", path);
		return CompareData(left.Volume, right.Volume);
	}

	private static async Task<(double p, double t, double u, double rho, double flow, double pressure)> CompareTransient(
		string cpuCasePath,
		CfdCaseDocument cpu,
		string gpuCasePath,
		CfdCaseDocument gpu)
	{
		CfdTransientResultReference leftReference = cpu.Results.Transient
			?? throw new InvalidDataException("The CPU benchmark did not produce a transient result.");
		CfdTransientResultReference rightReference = gpu.Results.Transient
			?? throw new InvalidDataException("The GPU benchmark did not produce a transient result.");
		string leftPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(cpuCasePath)!, leftReference.RelativePath));
		string rightPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(gpuCasePath)!, rightReference.RelativePath));
		using FgFlowResultSequence left = new(leftPath, leftReference.Sha256);
		using FgFlowResultSequence right = new(rightPath, rightReference.Sha256);
		if (left.FrameCount != right.FrameCount) throw new InvalidDataException("CPU and GPU frame counts differ.");
		double p = 0, t = 0, u = 0, rho = 0;
		List<double> leftPressure = [];
		List<double> rightPressure = [];
		for (int index = 0; index < left.FrameCount; ++index)
		{
			CfdResultFrame a = await left.LoadFrameAsync(index, CancellationToken.None);
			CfdResultFrame b = await right.LoadFrameAsync(index, CancellationToken.None);
			leftPressure.Add(OutletAreaAveragePressure(a.Results.Boundaries));
			rightPressure.Add(OutletAreaAveragePressure(b.Results.Boundaries));
		}
		int[] frames = [0, left.FrameCount / 4, left.FrameCount / 2, 3 * left.FrameCount / 4];
		foreach (int index in frames.Distinct())
		{
			CfdResultFrame a = await left.LoadFrameAsync(index, CancellationToken.None);
			CfdResultFrame b = await right.LoadFrameAsync(index, CancellationToken.None);
			(double fp, double ft, double fu, double frho) = CompareData(a.Results.Volume, b.Results.Volume);
			p = Math.Max(p, fp); t = Math.Max(t, ft); u = Math.Max(u, fu); rho = Math.Max(rho, frho);
		}
		double flow = Nrmse(
			cpu.Results.TransientSummary!.Frames.Select(value => value.NetOutletMassFlowKgPerSecond).ToArray(),
			gpu.Results.TransientSummary!.Frames.Select(value => value.NetOutletMassFlowKgPerSecond).ToArray(),
			1e-4,
			removeMean: false);
		double pressure = Nrmse(leftPressure.ToArray(), rightPressure.ToArray(), 100, removeMean: true);
		return (p, t, u, rho, flow, pressure);
	}

	private static double OutletAreaAveragePressure(IReadOnlyList<CfdBoundaryPatch> patches)
	{
		LegacyVtkDataSet data = patches.Single(value => value.Role == "outlet").Data;
		double weighted = 0, totalArea = 0;
		for (int index = 0; index < data.Cells.Length; ++index)
		{
			double area = CellArea(data, data.Cells[index]);
			double pressure = data.CellScalars.TryGetValue("p", out double[]? cells)
				? cells[index]
				: data.Cells[index].PointIndices.Average(point => data.PointScalars["p"][point]);
			weighted += pressure * area;
			totalArea += area;
		}
		if (!(totalArea > 0)) throw new InvalidDataException("The benchmark outlet has zero area.");
		return weighted / totalArea;
	}

	private static double CellArea(LegacyVtkDataSet data, VtkCell cell)
	{
		VtkVector origin = data.Points[cell.PointIndices[0]];
		double area = 0;
		for (int index = 1; index + 1 < cell.PointIndices.Length; ++index)
		{
			VtkVector a = data.Points[cell.PointIndices[index]] - origin;
			VtkVector b = data.Points[cell.PointIndices[index + 1]] - origin;
			area += 0.5 * VtkVector.Cross(a, b).Length;
		}
		return area;
	}

	private static double Nrmse(double[] baseline, double[] candidate, double floor, bool removeMean)
	{
		if (baseline.Length != candidate.Length || baseline.Length == 0)
			throw new InvalidDataException("CPU and GPU waveform sizes differ.");
		double baselineMean = removeMean ? baseline.Average() : 0;
		double candidateMean = removeMean ? candidate.Average() : 0;
		double difference = 0, reference = 0;
		for (int index = 0; index < baseline.Length; ++index)
		{
			double a = baseline[index] - baselineMean;
			double b = candidate[index] - candidateMean;
			difference += (b - a) * (b - a);
			reference += a * a;
		}
		return Math.Sqrt(difference / baseline.Length)
			/ Math.Max(Math.Sqrt(reference / baseline.Length), floor);
	}

	private static (double p, double t, double u, double rho) CompareData(
		LegacyVtkDataSet left,
		LegacyVtkDataSet right) =>
		(RelativeL2(Scalars(left, "p"), Scalars(right, "p")),
		 RelativeL2(Scalars(left, "T"), Scalars(right, "T")),
		 RelativeL2(Vectors(left, "U"), Vectors(right, "U")),
		 RelativeL2(Scalars(left, "rho"), Scalars(right, "rho")));

	private static double[] Scalars(LegacyVtkDataSet data, string field) =>
		data.PointScalars.TryGetValue(field, out double[]? point) ? point
		: data.CellScalars.TryGetValue(field, out double[]? cell) ? cell
		: throw new InvalidDataException($"Field '{field}' is missing from benchmark data.");

	private static VtkVector[] Vectors(LegacyVtkDataSet data, string field) =>
		data.PointVectors.TryGetValue(field, out VtkVector[]? point) ? point
		: data.CellVectors.TryGetValue(field, out VtkVector[]? cell) ? cell
		: throw new InvalidDataException($"Field '{field}' is missing from benchmark data.");

	private static double RelativeL2(double[] baseline, double[] candidate)
	{
		if (baseline.Length != candidate.Length) throw new InvalidDataException("Benchmark field sizes differ.");
		double difference = 0, reference = 0;
		for (int index = 0; index < baseline.Length; ++index)
		{
			double delta = candidate[index] - baseline[index];
			difference += delta * delta;
			reference += baseline[index] * baseline[index];
		}
		return Math.Sqrt(difference / Math.Max(reference, double.Epsilon));
	}

	private static double RelativeL2(VtkVector[] baseline, VtkVector[] candidate)
	{
		if (baseline.Length != candidate.Length) throw new InvalidDataException("Benchmark vector sizes differ.");
		double difference = 0, reference = 0;
		for (int index = 0; index < baseline.Length; ++index)
		{
			VtkVector delta = candidate[index] - baseline[index];
			difference += delta.Dot(delta);
			reference += baseline[index].Dot(baseline[index]);
		}
		return Math.Sqrt(difference / Math.Max(reference, double.Epsilon));
	}

	private static double IntegratedMetricError(CfdCaseDocument cpu, CfdCaseDocument gpu)
	{
		if (cpu.AnalysisMode == CfdAnalysisMode.Steady)
		{
			CfdResultSummary a = cpu.Results.Steady ?? throw new InvalidDataException("CPU steady summary is missing.");
			CfdResultSummary b = gpu.Results.Steady ?? throw new InvalidDataException("GPU steady summary is missing.");
			return Math.Max(Relative(a.PressureLossPa, b.PressureLossPa), Relative(a.MassImbalanceFraction, b.MassImbalanceFraction));
		}
		CfdTransientResultSummary ta = cpu.Results.TransientSummary
			?? throw new InvalidDataException("CPU transient summary is missing.");
		CfdTransientResultSummary tb = gpu.Results.TransientSummary
			?? throw new InvalidDataException("GPU transient summary is missing.");
		return Math.Max(Relative(ta.CycleAveragePressureLossPa, tb.CycleAveragePressureLossPa),
			Relative(ta.CycleMassImbalanceFraction, tb.CycleMassImbalanceFraction));
	}

	private static double Relative(double? baseline, double? candidate)
	{
		if (!baseline.HasValue && !candidate.HasValue) return 0;
		if (!baseline.HasValue || !candidate.HasValue) return double.PositiveInfinity;
		return Math.Abs(candidate.Value - baseline.Value) / Math.Max(Math.Abs(baseline.Value), 1e-12);
	}

	private static LoadedGasPackage LoadPackage(string casePath, CfdCaseDocument document)
	{
		string path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(casePath)!, document.SourcePackagePath));
		return GasPackageReader.Load(path);
	}
}
