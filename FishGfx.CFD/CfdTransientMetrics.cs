namespace FishGfx.CFD;

public readonly record struct CfdCycleMonitorSample(
	double CrankAngleDegrees,
	double OutletMassFlowKgPerSecond,
	double OutletPressurePa,
	double DomainMassKg);

public readonly record struct CfdTransientFluxSample(
	double TimeSeconds,
	double InletMassFlowKgPerSecond,
	double OutletMassFlowKgPerSecond,
	double InletMassWeightedTotalPressurePa,
	double OutletMassWeightedTotalPressurePa);

public static class CfdTransientPeriodicity
{
	public static CfdPeriodicityResult Compare(
		int comparedCycle,
		IReadOnlyList<CfdCycleMonitorSample> previous,
		IReadOnlyList<CfdCycleMonitorSample> current,
		CfdEngineTransientSettings settings)
	{
		settings.Validate();
		if (previous.Count != current.Count || previous.Count < 2)
			throw new ArgumentException("Periodicity histories must have equal non-trivial sample counts.");
		double[] previousFlow = previous.Select(value => value.OutletMassFlowKgPerSecond).ToArray();
		double[] currentFlow = current.Select(value => value.OutletMassFlowKgPerSecond).ToArray();
		double flowError = Nrmse(currentFlow, previousFlow, settings.FlowWaveformScaleFloorKgPerSecond, false);
		double previousMeanPressure = previous.Average(value => value.OutletPressurePa);
		double currentMeanPressure = current.Average(value => value.OutletPressurePa);
		double[] previousPressure = previous.Select(value => value.OutletPressurePa - previousMeanPressure).ToArray();
		double[] currentPressure = current.Select(value => value.OutletPressurePa - currentMeanPressure).ToArray();
		double pressureError = Nrmse(currentPressure, previousPressure, settings.PressureWaveformScaleFloorPa, true);
		double meanPressureChange = Math.Abs(currentMeanPressure - previousMeanPressure);
		double meanPressureTolerance = Math.Max(
			settings.MeanPressureAbsoluteTolerancePa,
			settings.MeanPressureRelativeTolerance * Math.Abs(currentMeanPressure));
		double currentMeanMass = current.Average(value => value.DomainMassKg);
		double domainMassError = Math.Abs(current[^1].DomainMassKg - previous[^1].DomainMassKg)
			/ Math.Max(currentMeanMass, settings.DomainMassFloorKg);
		return new CfdPeriodicityResult
		{
			ComparedCycle = comparedCycle,
			FlowWaveformNrmse = flowError,
			FlowWaveformTolerance = settings.FlowWaveformNrmseTolerance,
			PressureFluctuationNrmse = pressureError,
			PressureFluctuationTolerance = settings.PressureWaveformNrmseTolerance,
			MeanPressureChangePa = meanPressureChange,
			MeanPressureTolerancePa = meanPressureTolerance,
			DomainMassRelativeError = domainMassError,
			DomainMassRelativeTolerance = settings.DomainMassRelativeTolerance,
			Passed = flowError <= settings.FlowWaveformNrmseTolerance
				&& pressureError <= settings.PressureWaveformNrmseTolerance
				&& meanPressureChange <= meanPressureTolerance
				&& domainMassError <= settings.DomainMassRelativeTolerance,
		};
	}

	public static CfdCycleMonitorSample[] Resample(
		IReadOnlyList<CfdCycleMonitorSample> samples,
		double outputAngleDegrees)
	{
		if (samples.Count < 2 || !double.IsFinite(outputAngleDegrees) || outputAngleDegrees <= 0)
			throw new ArgumentException("A monitor history needs at least two samples and a positive output angle.");
		CfdCycleMonitorSample[] ordered = samples.OrderBy(value => value.CrankAngleDegrees).ToArray();
		int count = checked((int)Math.Round(720.0 / outputAngleDegrees));
		CfdCycleMonitorSample[] result = new CfdCycleMonitorSample[count];
		int right = 1;
		for (int index = 0; index < count; ++index)
		{
			double angle = index * outputAngleDegrees;
			while (right < ordered.Length - 1 && ordered[right].CrankAngleDegrees < angle) ++right;
			CfdCycleMonitorSample a = ordered[Math.Max(0, right - 1)];
			CfdCycleMonitorSample b = ordered[right];
			double span = b.CrankAngleDegrees - a.CrankAngleDegrees;
			double amount = span > 0 ? Math.Clamp((angle - a.CrankAngleDegrees) / span, 0, 1) : 0;
			result[index] = new(
				angle,
				Lerp(a.OutletMassFlowKgPerSecond, b.OutletMassFlowKgPerSecond, amount),
				Lerp(a.OutletPressurePa, b.OutletPressurePa, amount),
				Lerp(a.DomainMassKg, b.DomainMassKg, amount));
		}
		return result;
	}

	private static double Nrmse(double[] current, double[] previous, double floor, bool zeroMean)
	{
		double difference = Math.Sqrt(current.Zip(previous, (a, b) => (a - b) * (a - b)).Average());
		double[] reference = zeroMean
			? previous.Select(value => value - previous.Average()).ToArray()
			: previous;
		double scale = Math.Max(Math.Sqrt(reference.Select(value => value * value).Average()), floor);
		return difference / scale;
	}

	private static double Lerp(double a, double b, double amount) => a + (b - a) * amount;
}

public static class CfdTransientMetricCalculator
{
	public static CfdTransientMetricState Classify(
		double inletMassFlowKgPerSecond,
		double outletMassFlowKgPerSecond,
		double minimumMagnitude)
	{
		if (inletMassFlowKgPerSecond < -minimumMagnitude || outletMassFlowKgPerSecond < -minimumMagnitude)
			return CfdTransientMetricState.ReverseFlow;
		if (inletMassFlowKgPerSecond <= minimumMagnitude || outletMassFlowKgPerSecond <= minimumMagnitude)
			return CfdTransientMetricState.Unavailable;
		return CfdTransientMetricState.Valid;
	}

	public static double? CycleAveragePressureLoss(
		IReadOnlyList<CfdTransientFluxSample> samples,
		double minimumMassFlowKgPerSecond)
	{
		if (samples.Count < 2) return null;
		double inletMass = 0;
		double outletMass = 0;
		double inletPressureMass = 0;
		double outletPressureMass = 0;
		for (int index = 1; index < samples.Count; ++index)
		{
			CfdTransientFluxSample a = samples[index - 1];
			CfdTransientFluxSample b = samples[index];
			double dt = b.TimeSeconds - a.TimeSeconds;
			if (!(dt > 0)) throw new InvalidDataException("Transient metric samples must have increasing times.");
			inletMass += 0.5 * (a.InletMassFlowKgPerSecond + b.InletMassFlowKgPerSecond) * dt;
			outletMass += 0.5 * (a.OutletMassFlowKgPerSecond + b.OutletMassFlowKgPerSecond) * dt;
			inletPressureMass += 0.5 * (
				a.InletMassWeightedTotalPressurePa * a.InletMassFlowKgPerSecond
				+ b.InletMassWeightedTotalPressurePa * b.InletMassFlowKgPerSecond) * dt;
			outletPressureMass += 0.5 * (
				a.OutletMassWeightedTotalPressurePa * a.OutletMassFlowKgPerSecond
				+ b.OutletMassWeightedTotalPressurePa * b.OutletMassFlowKgPerSecond) * dt;
		}
		double duration = samples[^1].TimeSeconds - samples[0].TimeSeconds;
		double massFloor = minimumMassFlowKgPerSecond * duration;
		if (inletMass <= massFloor || outletMass <= massFloor) return null;
		return inletPressureMass / inletMass - outletPressureMass / outletMass;
	}
}
