using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace FishGfx.CFD;

public readonly record struct CfdPulseSample(double TimeSeconds, double Value);

public sealed record CfdCylinderPulseTable(
	int CylinderNumber,
	string ComponentId,
	double FiringPhaseDegrees,
	double EventMassKg,
	CfdPulseSample[] MassFlow,
	CfdPulseSample[] Temperature)
{
	public double PeakMassFlowKgPerSecond => MassFlow.Max(value => value.Value);
}

public sealed record CfdTransientPulseSet(
	double CycleDurationSeconds,
	double SecondsPerDegree,
	CfdCylinderPulseTable[] Cylinders)
{
	public string Sha256()
	{
		using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		foreach (CfdCylinderPulseTable cylinder in Cylinders.OrderBy(value => value.CylinderNumber))
		{
			Append(hash, cylinder.CylinderNumber.ToString(CultureInfo.InvariantCulture));
			Append(hash, cylinder.ComponentId);
			foreach (CfdPulseSample sample in cylinder.MassFlow)
			{
				Append(hash, sample.TimeSeconds.ToString("R", CultureInfo.InvariantCulture));
				Append(hash, sample.Value.ToString("R", CultureInfo.InvariantCulture));
			}
			foreach (CfdPulseSample sample in cylinder.Temperature)
				Append(hash, sample.Value.ToString("R", CultureInfo.InvariantCulture));
		}
		return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
	}

	private static void Append(IncrementalHash hash, string value) =>
		hash.AppendData(Encoding.UTF8.GetBytes(value + "\n"));
}

public static class CfdTransientPulseGenerator
{
	public static CfdTransientPulseSet Generate(
		CfdEngineTransientSettings settings,
		CfdSolverSettings solver)
	{
		settings.Validate();
		solver.Validate();
		if (!string.Equals(settings.PulsePreset, CfdEngineTransientSettings.PulsePresetId, StringComparison.Ordinal))
			throw new InvalidDataException($"Unsupported transient pulse preset '{settings.PulsePreset}'.");

		double secondsPerDegree = settings.SecondsPerDegree;
		double cycleDuration = settings.CycleDurationSeconds;
		double eventMass = solver.TotalMassFlowKgPerSecond * cycleDuration
			/ settings.CylinderAssignments.Count;
		int samplesPerCycle = checked((int)Math.Round(720.0 / settings.PulseTableStepDegrees));
		int sampleCount = checked(samplesPerCycle * settings.MaximumCycles + 1);
		double sampleTime = settings.PulseTableStepDegrees * secondsPerDegree;
		Dictionary<int, double> phases = FiringPhases(settings.FiringOrder);
		List<CfdCylinderPulseTable> cylinders = [];

		foreach (CfdCylinderAssignment assignment in settings.CylinderAssignments.OrderBy(value => value.CylinderNumber))
		{
			double phase = phases[assignment.CylinderNumber];
			double[] oneCycleRaw = Enumerable.Range(0, samplesPerCycle + 1)
				.Select(index => RawPulse(index * settings.PulseTableStepDegrees, phase, settings))
				.ToArray();
			double rawIntegral = TrapezoidalIntegral(oneCycleRaw, sampleTime);
			if (!(rawIntegral > 0)) throw new InvalidDataException("The transient pulse has zero discrete area.");
			double scale = eventMass / rawIntegral;
			double startupMassFlow = settings.InitialisationMode == TransientInitialisationMode.CompatibleSteadyResult
				? solver.RunnerMassFlows.TryGetValue(assignment.ComponentId, out double configured)
					? configured
					: solver.TotalMassFlowKgPerSecond / settings.CylinderAssignments.Count
				: 0;
			double rampEndDegrees = settings.StartupRampCycles * 720.0;
			CfdPulseSample[] massFlow = new CfdPulseSample[sampleCount + 1];
			CfdPulseSample[] temperature = new CfdPulseSample[sampleCount + 1];
			for (int index = 0; index < sampleCount; ++index)
			{
				double degrees = index * settings.PulseTableStepDegrees;
				double time = degrees * secondsPerDegree;
				double target = RawPulse(degrees, phase, settings) * scale;
				double ramp = SmoothStep(Math.Clamp(degrees / rampEndDegrees, 0, 1));
				massFlow[index] = new(time, startupMassFlow + (target - startupMassFlow) * ramp);
				temperature[index] = new(time, solver.InletTemperatureK);
			}
			// The maximum-cycle endpoint is periodic. The distinct guard beyond the run prevents clamping
			// to a non-zero value if a diagnostic utility evaluates just past endTime.
			double guardTime = settings.MaximumCycles * cycleDuration + sampleTime;
			massFlow[^1] = new(guardTime, 0);
			temperature[^1] = new(guardTime, solver.InletTemperatureK);
			cylinders.Add(new(
				assignment.CylinderNumber,
				assignment.ComponentId,
				phase,
				eventMass,
				massFlow,
				temperature));
		}
		return new(cycleDuration, secondsPerDegree, cylinders.ToArray());
	}

	public static string OpenFoamTable(IEnumerable<CfdPulseSample> samples)
	{
		StringBuilder result = new();
		result.AppendLine("{");
		result.AppendLine("        type table;");
		result.AppendLine("        values");
		result.AppendLine("        (");
		foreach (CfdPulseSample sample in samples)
		{
			result.Append("            (")
				.Append(sample.TimeSeconds.ToString("R", CultureInfo.InvariantCulture))
				.Append(' ')
				.Append(sample.Value.ToString("R", CultureInfo.InvariantCulture))
				.AppendLine(")");
		}
		result.AppendLine("        );");
		result.Append("    }");
		return result.ToString();
	}

	internal static double IntegrateMassOverCycle(
		CfdCylinderPulseTable table,
		int samplesPerCycle,
		int zeroBasedCycle = 0)
	{
		return TrapezoidalIntegral(table.MassFlow
			.Skip(zeroBasedCycle * samplesPerCycle)
			.Take(samplesPerCycle + 1)
			.Select(value => value.Value).ToArray(),
			table.MassFlow[1].TimeSeconds - table.MassFlow[0].TimeSeconds);
	}

	private static Dictionary<int, double> FiringPhases(IReadOnlyList<int> firingOrder)
	{
		Dictionary<int, double> result = [];
		for (int index = 0; index < firingOrder.Count; ++index)
			result.Add(firingOrder[index], index * 720.0 / firingOrder.Count);
		return result;
	}

	private static double RawPulse(double globalDegrees, double firingPhase, CfdEngineTransientSettings settings)
	{
		double local = Mod(globalDegrees - firingPhase, 720.0);
		if (local < settings.EventStartDegreesAfterFiring
			|| local > settings.EventEndDegreesAfterFiring)
		{
			return 0;
		}
		double x = (local - settings.EventStartDegreesAfterFiring)
			/ (settings.EventEndDegreesAfterFiring - settings.EventStartDegreesAfterFiring);
		return x * x * Math.Pow(1 - x, 5);
	}

	private static double TrapezoidalIntegral(IReadOnlyList<double> values, double step)
	{
		double sum = 0;
		for (int index = 1; index < values.Count; ++index)
			sum += (values[index - 1] + values[index]) * 0.5 * step;
		return sum;
	}

	private static double Mod(double value, double period)
	{
		double result = value % period;
		return result < 0 ? result + period : result;
	}

	private static double SmoothStep(double value) => value * value * (3 - 2 * value);
}
