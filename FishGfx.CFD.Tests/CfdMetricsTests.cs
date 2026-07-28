using FishGfx.CFD;
using Xunit;

namespace FishGfx.CFD.Tests;

public sealed class CfdMetricsTests
{
	[Fact]
	public void PressureLossUsesMassFluxWeightedTotalPressure()
	{
		LegacyVtkDataSet inletA = Patch(110000, 1.0, 0.1, new VtkVector(0, 0, -2));
		LegacyVtkDataSet inletB = Patch(108000, 2.0, 0.1, new VtkVector(0, 0, -1));
		LegacyVtkDataSet outlet = Patch(100000, 1.0, 0.1, new VtkVector(0, 0, 4));
		LegacyVtkDataSet walls = Patch(100000, 1.0, 0, default);
		walls.CellScalars["yPlus"] = [20];
		CfdResultSummary result = CfdMetrics.Calculate(
			[
				new("a", "inlet", inletA),
				new("b", "inlet", inletB),
				new("out", "outlet", outlet),
				new("walls", "walls", walls),
			],
			CfdFluidPreset.IdealAirExhaustV1,
			CfdRunStatus.Converged);
		Assert.NotNull(result.PressureLossPa);
		Assert.InRange(result.PressureLossPa!.Value, 8500, 9500);
		Assert.Equal(20, result.YPlusAreaWeightedMean);
		Assert.Equal(1, result.WallAreaBelowTargetFraction);
	}

	private static LegacyVtkDataSet Patch(double p, double rho, double ma, VtkVector velocity)
	{
		LegacyVtkDataSet result = new()
		{
			Points = [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)],
			Cells = [new VtkCell(5, [0, 1, 2])],
		};
		result.CellScalars["p"] = [p];
		result.CellScalars["rho"] = [rho];
		result.CellScalars["T"] = [p / (CfdFluidPreset.IdealAirExhaustV1.SpecificGasConstant * rho)];
		result.CellScalars["Ma"] = [ma];
		result.CellVectors["U"] = [velocity];
		return result;
	}
}
