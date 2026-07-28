namespace FishGfx.CFD;

public sealed record CfdBoundaryPatch(string Name, string Role, LegacyVtkDataSet Data);

public static class CfdMetrics
{
	public static CfdResultSummary Calculate(
		IReadOnlyList<CfdBoundaryPatch> patches,
		CfdFluidPreset fluid,
		CfdRunStatus status)
	{
		fluid.Validate();
		CfdBoundaryPatch[] inlets = patches.Where(patch => patch.Role == "inlet").ToArray();
		CfdBoundaryPatch outlet = patches.Single(patch => patch.Role == "outlet");
		FluxIntegral inlet = inlets.Select(patch => IntegrateFlux(patch.Data, -1, fluid)).Aggregate(Add);
		FluxIntegral outletFlux = IntegrateFlux(outlet.Data, 1, fluid);
		if (inlet.NetMassFlow <= 0 || outletFlux.NetMassFlow <= 0)
		{
			throw new InvalidDataException("Pressure loss requires positive net inlet and outlet mass flow.");
		}
		double imbalance = Math.Abs(inlet.NetMassFlow - outletFlux.NetMassFlow)
			/ Math.Max(inlet.NetMassFlow, outletFlux.NetMassFlow);
		double pressureLoss = inlet.TotalPressureFlux / inlet.NetMassFlow
			- outletFlux.TotalPressureFlux / outletFlux.NetMassFlow;
		double reverse = (inlet.ReverseMassFlow + outletFlux.ReverseMassFlow)
			/ Math.Max(inlet.NetMassFlow + outletFlux.NetMassFlow, double.Epsilon);
		double densityConsistency = patches
			.Where(patch => patch.Role is "inlet" or "outlet")
			.Max(patch => DensityConsistency(patch.Data, fluid));
		CfdBoundaryPatch? walls = patches.FirstOrDefault(patch => patch.Role == "walls");
		YPlusStatistics? yPlus = walls is null ? null : IntegrateYPlus(walls.Data);
		List<string> diagnostics = [];
		if (imbalance > 0.01) diagnostics.Add("Mass imbalance exceeds 1%.");
		if (yPlus != null && yPlus.BelowFraction + yPlus.AboveFraction > 0.10)
		{
			diagnostics.Add(
				"Wall y+ is outside the 30-300 nutkWallFunction target band over more than 10% of wall area.");
		}
		if (densityConsistency > 0.01)
			diagnostics.Add("Solver rho differs from the ideal-gas p/(R*T) consistency check by more than 1%.");
		return new CfdResultSummary
		{
			Status = status,
			MassImbalanceFraction = imbalance,
			PressureLossPa = pressureLoss,
			BackflowFraction = reverse,
			YPlusMinimum = yPlus?.Minimum,
			YPlusAreaWeightedMean = yPlus?.Mean,
			YPlusMaximum = yPlus?.Maximum,
			WallAreaBelowTargetFraction = yPlus?.BelowFraction,
			WallAreaAboveTargetFraction = yPlus?.AboveFraction,
			DensityConsistencyMaximumRelativeError = densityConsistency,
			Diagnostic = diagnostics.Count == 0 ? null : string.Join(" ", diagnostics),
		};
	}

	private static double DensityConsistency(LegacyVtkDataSet data, CfdFluidPreset fluid)
	{
		double maximum = 0;
		for (int index = 0; index < data.Cells.Length; ++index)
		{
			double p = Scalar(data, "p", index);
			double temperature = Scalar(data, "T", index);
			double rho = Scalar(data, "rho", index);
			double expected = p / (fluid.SpecificGasConstant * temperature);
			maximum = Math.Max(maximum, Math.Abs(rho - expected) / Math.Max(Math.Abs(rho), double.Epsilon));
		}
		return maximum;
	}

	private static FluxIntegral IntegrateFlux(LegacyVtkDataSet data, int direction, CfdFluidPreset fluid)
	{
		double net = 0;
		double reverse = 0;
		double totalPressureFlux = 0;
		for (int index = 0; index < data.Cells.Length; ++index)
		{
			(VtkVector areaVector, double area) = AreaVector(data, data.Cells[index]);
			double p = Scalar(data, "p", index);
			double rho = Scalar(data, "rho", index);
			double mach = Scalar(data, "Ma", index);
			VtkVector velocity = Vector(data, "U", index);
			double signedMassFlow = direction * rho * velocity.Dot(areaVector);
			double p0 = p * Math.Pow(
				1 + (fluid.Gamma - 1) * mach * mach / 2,
				fluid.Gamma / (fluid.Gamma - 1));
			net += signedMassFlow;
			totalPressureFlux += p0 * signedMassFlow;
			if (signedMassFlow < 0) reverse += -signedMassFlow;
			if (!(area > 0) || !double.IsFinite(p0)) throw new InvalidDataException("Invalid boundary face metric.");
		}
		return new(net, reverse, totalPressureFlux);
	}

	private static YPlusStatistics IntegrateYPlus(LegacyVtkDataSet data)
	{
		double areaSum = 0;
		double weighted = 0;
		double below = 0;
		double above = 0;
		double minimum = double.PositiveInfinity;
		double maximum = double.NegativeInfinity;
		for (int index = 0; index < data.Cells.Length; ++index)
		{
			double area = AreaVector(data, data.Cells[index]).Area;
			double value = Scalar(data, "yPlus", index);
			areaSum += area;
			weighted += value * area;
			if (value < 30) below += area;
			if (value > 300) above += area;
			minimum = Math.Min(minimum, value);
			maximum = Math.Max(maximum, value);
		}
		if (!(areaSum > 0)) throw new InvalidDataException("The wall VTK dataset has no positive-area faces.");
		return new(minimum, weighted / areaSum, maximum, below / areaSum, above / areaSum);
	}

	private static (VtkVector Vector, double Area) AreaVector(LegacyVtkDataSet data, VtkCell cell)
	{
		if (cell.PointIndices.Length < 3) throw new InvalidDataException("A boundary cell has fewer than three points.");
		VtkVector origin = data.Points[cell.PointIndices[0]];
		VtkVector sum = default;
		for (int index = 1; index + 1 < cell.PointIndices.Length; ++index)
		{
			VtkVector cross = VtkVector.Cross(
				data.Points[cell.PointIndices[index]] - origin,
				data.Points[cell.PointIndices[index + 1]] - origin);
			sum = new(sum.X + cross.X / 2, sum.Y + cross.Y / 2, sum.Z + cross.Z / 2);
		}
		return (sum, sum.Length);
	}

	private static double Scalar(LegacyVtkDataSet data, string name, int cell)
	{
		if (data.CellScalars.TryGetValue(name, out double[]? cellValues)) return cellValues[cell];
		if (data.PointScalars.TryGetValue(name, out double[]? pointValues))
		{
			return data.Cells[cell].PointIndices.Average(index => pointValues[index]);
		}
		throw new InvalidDataException($"Boundary VTK field '{name}' is missing.");
	}

	private static VtkVector Vector(LegacyVtkDataSet data, string name, int cell)
	{
		if (data.CellVectors.TryGetValue(name, out VtkVector[]? cellValues)) return cellValues[cell];
		if (data.PointVectors.TryGetValue(name, out VtkVector[]? pointValues))
		{
			int[] points = data.Cells[cell].PointIndices;
			return new(
				points.Average(index => pointValues[index].X),
				points.Average(index => pointValues[index].Y),
				points.Average(index => pointValues[index].Z));
		}
		throw new InvalidDataException($"Boundary VTK vector field '{name}' is missing.");
	}

	private static FluxIntegral Add(FluxIntegral left, FluxIntegral right) => new(
		left.NetMassFlow + right.NetMassFlow,
		left.ReverseMassFlow + right.ReverseMassFlow,
		left.TotalPressureFlux + right.TotalPressureFlux);

	private readonly record struct FluxIntegral(
		double NetMassFlow,
		double ReverseMassFlow,
		double TotalPressureFlux);
	private sealed record YPlusStatistics(
		double Minimum,
		double Mean,
		double Maximum,
		double BelowFraction,
		double AboveFraction);
}
