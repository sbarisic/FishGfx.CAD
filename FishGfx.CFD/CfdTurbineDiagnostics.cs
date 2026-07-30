namespace FishGfx.CFD;

internal static class CfdTurbineDiagnostics
{
	internal static (CfdTurbineFrameDiagnostic Diagnostic, double MeanPressurePa) Calculate(
		int frameIndex,
		double crankAngleDegrees,
		double outletMassFlowKgPerSecond,
		LegacyVtkDataSet outlet,
		IReadOnlyList<CfdTurbineCurvePoint> curve,
		CfdTurbineBoundarySettings settings,
		double? fixedPreviewPressureDropPa = null)
	{
		double density = MeanScalar(outlet, "rho");
		double pressure = MeanScalar(outlet, "p");
		double volumeFlow = density > 0 ? outletMassFlowKgPerSecond / density : 0;
		CfdTurbineMapRangeState state;
		double pressureDrop;
		if (volumeFlow <= 0)
		{
			state = CfdTurbineMapRangeState.ReverseFlow;
			pressureDrop = 0;
		}
		else
		{
			CfdTurbineCurvePoint firstPublished = curve.First(value => value.Published);
			CfdTurbineCurvePoint lastPublished = curve.Last(value => value.Published);
			state = volumeFlow < firstPublished.VolumeFlowCubicMetersPerSecond
				? CfdTurbineMapRangeState.BelowPublishedRange
				: volumeFlow <= lastPublished.VolumeFlowCubicMetersPerSecond
					? CfdTurbineMapRangeState.WithinPublishedRange
					: CfdTurbineMapRangeState.AbovePublishedRange;
			pressureDrop = fixedPreviewPressureDropPa
				?? -Interpolate(curve, volumeFlow);
		}
		return (
			new CfdTurbineFrameDiagnostic
			{
				FrameIndex = frameIndex,
				CrankAngleDegrees = crankAngleDegrees,
				VolumeFlowCubicMetersPerSecond = volumeFlow,
				EstimatedPressureDropPa = pressureDrop,
				EstimatedPressureRatio = 1 + pressureDrop / settings.DischargePressurePa,
				RangeState = state,
			},
			pressure);
	}

	private static double Interpolate(IReadOnlyList<CfdTurbineCurvePoint> curve, double q)
	{
		if (q <= curve[0].VolumeFlowCubicMetersPerSecond) return curve[0].FanCurvePressurePa;
		for (int index = 1; index < curve.Count; ++index)
		{
			if (q > curve[index].VolumeFlowCubicMetersPerSecond) continue;
			CfdTurbineCurvePoint a = curve[index - 1];
			CfdTurbineCurvePoint b = curve[index];
			double amount = (q - a.VolumeFlowCubicMetersPerSecond)
				/ (b.VolumeFlowCubicMetersPerSecond - a.VolumeFlowCubicMetersPerSecond);
			return a.FanCurvePressurePa + amount * (b.FanCurvePressurePa - a.FanCurvePressurePa);
		}
		throw new InvalidDataException("Outlet flow exceeded the turbine-map 102% limit.");
	}

	private static double MeanScalar(LegacyVtkDataSet data, string field)
	{
		if (data.PointScalars.TryGetValue(field, out double[]? points) && points.Length > 0)
			return points.Average();
		if (data.CellScalars.TryGetValue(field, out double[]? cells) && cells.Length > 0)
			return cells.Average();
		throw new InvalidDataException($"Outlet field '{field}' is unavailable for turbine diagnostics.");
	}
}
