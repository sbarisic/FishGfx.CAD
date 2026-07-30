using System.Globalization;
using System.Text;

namespace FishGfx.CFD;

public readonly record struct CfdTurbineDigitizedPoint(
	double PixelX,
	double PixelY,
	double CorrectedMassFlowKgPerSecond,
	double PressureRatio);

public readonly record struct CfdTurbineCurvePoint(
	double VolumeFlowCubicMetersPerSecond,
	double FanCurvePressurePa,
	double PressureRatio,
	double CorrectedMassFlowKgPerSecond,
	bool Published);

public sealed record CfdTurbineMapPreset(
	string Id,
	string Manufacturer,
	string Model,
	double HousingAreaRatio,
	string InstalledInlet,
	string InstalledOutlet,
	string PublishedCurveLabel,
	string SourceUrl,
	string SourceSha256,
	int SourceImageWidth,
	int SourceImageHeight,
	double PlotLeftPixel,
	double PlotRightPixel,
	double PlotTopPixel,
	double PlotBottomPixel,
	double PressureRatioMinimum,
	double PressureRatioMaximum,
	double CorrectedFlowMaximumKgPerSecond,
	double ReferenceTemperatureK,
	double ReferencePressurePa,
	string PublishedPressureRatioDefinition,
	string ModelInterpretation,
	string CurveVolumeBasis,
	string OpenFoamCurveInput,
	string Approximation,
	int DigitizerVersion,
	int ConversionVersion,
	int RegularizationVersion,
	CfdTurbineDigitizedPoint[] RawPoints);

public static class CfdTurbineMaps
{
	private const double MergeAbsoluteTolerance = 1e-6;
	private const double MergeRelativeTolerance = 1e-5;

	public static CfdTurbineMapPreset GarrettG25550Point49ProxyV1 { get; } = new(
		CfdTurbineBoundarySettings.GarrettG25550PresetId,
		"Garrett Motion",
		"G25-550",
		0.49,
		"V-band (installed hardware metadata)",
		"V-band",
		"84 TRIM T25 0.49 A/R",
		"https://www.garrettmotion.com/wp-content/uploads/2022/06/Turbine-Flow-Maps-G25-2-scaled.jpg",
		"f600cc3a1171b117e5a8b29928337223a3ac7add3d0a178fff63d2d02f50e6c5",
		2048,
		1248,
		217,
		2003,
		115,
		1078,
		1.0,
		4.0,
		30.0 * 0.45359237 / 60.0,
		288.15,
		101325,
		"Unspecified by source image",
		"Turbine-inlet total pressure / discharge static pressure",
		"Turbine-inlet total-state ideal-gas volume",
		"Outlet-patch local static sum(phi/rho)",
		"Patch static volume approximates turbine-inlet total-state volume at the configured exhaust temperature.",
		1,
		1,
		1,
		[
			new(404, 765, 0.07371464787642784, 1.3141097424412094),
			new(516, 694, 0.09043586193146418, 1.502239641657335),
			new(625, 654, 0.0998562642159917, 1.685330347144457),
			new(814, 615, 0.10904115644340602, 2.002799552071669),
			new(1013, 596, 0.11351584752855659, 2.337066069428891),
			new(1107, 592, 0.11445788775700935, 2.4949608062709965),
			new(1409, 574, 0.11869706878504674, 3.0022396416573347),
			new(1708, 571, 0.11940359895638629, 3.50447928331467),
			new(2003, 570, 0.11963910901349949, 4),
		]);

	public static CfdTurbineMapPreset Resolve(string? id)
	{
		if (string.Equals(id, GarrettG25550Point49ProxyV1.Id, StringComparison.Ordinal))
			return GarrettG25550Point49ProxyV1;
		throw new InvalidDataException($"Unsupported turbine-map preset '{id}'.");
	}

	public static CfdTurbineCurvePoint[] BuildFanCurve(
		CfdTurbineMapPreset preset,
		CfdFluidPreset fluid,
		CfdTurbineBoundarySettings settings)
	{
		fluid.Validate();
		settings.Validate();
		List<(double Q, double PressureRatio, double CorrectedFlow)> converted = [];
		double runningFlow = 0;
		foreach (CfdTurbineDigitizedPoint raw in preset.RawPoints.OrderBy(value => value.PixelX))
		{
			double calibratedPressureRatio = Lerp(
				preset.PressureRatioMinimum,
				preset.PressureRatioMaximum,
				(raw.PixelX - preset.PlotLeftPixel) / (preset.PlotRightPixel - preset.PlotLeftPixel));
			double calibratedCorrectedFlow = preset.CorrectedFlowMaximumKgPerSecond
				* (preset.PlotBottomPixel - raw.PixelY)
				/ (preset.PlotBottomPixel - preset.PlotTopPixel);
			if (Math.Abs(calibratedPressureRatio - raw.PressureRatio) > 1e-12
				|| Math.Abs(calibratedCorrectedFlow - raw.CorrectedMassFlowKgPerSecond) > 1e-12)
			{
				throw new InvalidDataException("Stored turbine engineering data does not match its pixel calibration.");
			}
			double pressureRatio = raw.PressureRatio;
			double correctedFlow = raw.CorrectedMassFlowKgPerSecond;
			runningFlow = Math.Max(runningFlow, correctedFlow);
			double q = runningFlow * fluid.SpecificGasConstant
				* Math.Sqrt(settings.ExhaustTotalTemperatureK * preset.ReferenceTemperatureK)
				/ preset.ReferencePressurePa;
			converted.Add((q, pressureRatio, runningFlow));
		}

		List<(double Q, double PressureRatio, double CorrectedFlow)> regularized = [];
		foreach (var point in converted)
		{
			if (regularized.Count == 0)
			{
				regularized.Add(point);
				continue;
			}
			var previous = regularized[^1];
			double tolerance = Math.Max(MergeAbsoluteTolerance, MergeRelativeTolerance * point.Q);
			if (point.Q - previous.Q <= tolerance)
			{
				regularized[^1] = point.PressureRatio >= previous.PressureRatio ? point : previous;
			}
			else
			{
				regularized.Add(point);
			}
		}
		if (regularized.Count < 2)
			throw new InvalidDataException("The turbine map does not contain two distinct volumetric-flow points.");

		List<CfdTurbineCurvePoint> result =
		[
			new(0, 0, 1, 0, false),
		];
		result.AddRange(regularized.Select(point => new CfdTurbineCurvePoint(
			point.Q,
			-(point.PressureRatio - 1) * settings.DischargePressurePa,
			point.PressureRatio,
			point.CorrectedFlow,
			true)));
		CfdTurbineCurvePoint a = result[^2];
		CfdTurbineCurvePoint b = result[^1];
		double limitQ = b.VolumeFlowCubicMetersPerSecond * 1.02;
		double slope = (b.FanCurvePressurePa - a.FanCurvePressurePa)
			/ (b.VolumeFlowCubicMetersPerSecond - a.VolumeFlowCubicMetersPerSecond);
		double limitPressure = b.FanCurvePressurePa
			+ slope * (limitQ - b.VolumeFlowCubicMetersPerSecond);
		result.Add(new(limitQ, limitPressure, double.NaN, double.NaN, false));
		if (result.Zip(result.Skip(1)).Any(pair =>
			pair.First.VolumeFlowCubicMetersPerSecond >= pair.Second.VolumeFlowCubicMetersPerSecond))
		{
			throw new InvalidDataException("The generated turbine fan curve is not strictly increasing in volume flow.");
		}
		return result.ToArray();
	}

	public static string Csv(IReadOnlyList<CfdTurbineCurvePoint> points)
	{
		StringBuilder result = new("Q_m3_per_s,fanCurve_Pa\n");
		foreach (CfdTurbineCurvePoint point in points)
		{
			result.Append(point.VolumeFlowCubicMetersPerSecond.ToString("R", CultureInfo.InvariantCulture))
				.Append(',')
				.Append(point.FanCurvePressurePa.ToString("R", CultureInfo.InvariantCulture))
				.Append('\n');
		}
		return result.ToString();
	}

	public static string OpenFoamSolverCsv(IReadOnlyList<CfdTurbineCurvePoint> points)
	{
		if (points.Count < 3 || points[^1].Published || !points[^2].Published)
			throw new InvalidDataException("The turbine curve is missing its synthetic limit endpoint.");
		CfdTurbineCurvePoint[] solverPoints = points.ToArray();
		// The published curve is choked at its tail. Linear inversion through the last two
		// digitized points makes the 102% validation endpoint nearly vertical and is useful
		// for range diagnostics, but applying that extrapolated pressure during a nonlinear
		// startup iteration destabilizes fanPressure. Saturate the solver pressure at the
		// final published PR=4 value; accepted frames still use the extrapolated 102% gate.
		solverPoints[^1] = solverPoints[^1] with
		{
			FanCurvePressurePa = solverPoints[^2].FanCurvePressurePa,
		};
		return Csv(solverPoints);
	}

	public static double EstimatePressureRatioForActualMassFlow(
		CfdTurbineMapPreset preset,
		CfdTurbineBoundarySettings settings,
		double actualMassFlowKgPerSecond)
	{
		if (!double.IsFinite(actualMassFlowKgPerSecond) || actualMassFlowKgPerSecond < 0)
			throw new ArgumentOutOfRangeException(nameof(actualMassFlowKgPerSecond));
		double temperatureCorrection = Math.Sqrt(
			settings.ExhaustTotalTemperatureK / preset.ReferenceTemperatureK);
		List<(double MassFlow, double PressureRatio)> capacity =
		[
			(0, 1),
		];
		capacity.AddRange(preset.RawPoints.Select(point => (
			point.CorrectedMassFlowKgPerSecond * point.PressureRatio / temperatureCorrection,
			point.PressureRatio)));
		for (int index = 1; index < capacity.Count; ++index)
		{
			if (actualMassFlowKgPerSecond > capacity[index].MassFlow) continue;
			(double massA, double ratioA) = capacity[index - 1];
			(double massB, double ratioB) = capacity[index];
			double amount = (actualMassFlowKgPerSecond - massA) / (massB - massA);
			return Lerp(ratioA, ratioB, amount);
		}
		return capacity[^1].PressureRatio;
	}

	private static double Lerp(double minimum, double maximum, double amount) =>
		minimum + (maximum - minimum) * amount;
}
