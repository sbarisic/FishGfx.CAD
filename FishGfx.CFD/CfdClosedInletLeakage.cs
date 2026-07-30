using System.Globalization;

namespace FishGfx.CFD;

internal static class CfdClosedInletLeakage
{
	internal const double ClosedPulseToleranceKgPerSecond = 1e-12;
	internal const double SumToleranceKgPerSecond = 1e-8;
	internal const double OutwardFaceToleranceKgPerSecond = 1e-10;

	internal static CfdClosedInletLeakageSummary VerifyAcceptedCycle(
		string resultDirectory,
		GasPathManifest path,
		CfdTransientPulseSet pulses,
		int acceptedCycle,
		CfdEngineTransientSettings settings)
	{
		string root = Path.Combine(resultDirectory, "postProcessing");
		double cycleStart = (acceptedCycle - 1) * settings.CycleDurationSeconds;
		int sampleCount = checked((int)Math.Round(720.0 / settings.SolverAlignmentDegrees));
		GasOpeningManifest[] inlets = path.Openings.Where(value => value.Role == "inlet")
			.OrderBy(value => value.PatchName, StringComparer.Ordinal).ToArray();
		int checkedSamples = 0;
		double maximumAbsoluteTotal = 0;
		double maximumOutwardFace = 0;
		for (int inletIndex = 0; inletIndex < inlets.Length; ++inletIndex)
		{
			GasOpeningManifest inlet = inlets[inletIndex];
			CfdCylinderPulseTable pulse = pulses.Cylinders.Single(value => value.ComponentId == inlet.ComponentId);
			TimeValue[] sums = ReadSeries(root, $"inletPhiSum{inletIndex}");
			TimeValue[] maxima = ReadSeries(root, $"inletPhiMax{inletIndex}");
			for (int sample = 0; sample < sampleCount; ++sample)
			{
				double angle = sample * settings.SolverAlignmentDegrees;
				double time = cycleStart + angle * settings.SecondsPerDegree;
				if (Math.Abs(CfdTransientPulseGenerator.Interpolate(pulse.MassFlow, time))
					> ClosedPulseToleranceKgPerSecond) continue;
				double sum = Interpolate(sums, time);
				double maximum = Interpolate(maxima, time);
				++checkedSamples;
				maximumAbsoluteTotal = Math.Max(maximumAbsoluteTotal, Math.Abs(sum));
				maximumOutwardFace = Math.Max(maximumOutwardFace, maximum);
				if (Math.Abs(sum) > SumToleranceKgPerSecond || maximum > OutwardFaceToleranceKgPerSecond)
				{
					throw new InvalidDataException(FormattableString.Invariant(
						$"Closed inlet leakage at cylinder {pulse.CylinderNumber}, {angle:R} deg: sum(phi)={sum:R} kg/s, max outward face phi={maximum:R} kg/s."));
				}
			}
		}
		return new CfdClosedInletLeakageSummary
		{
			ClosedSamplesChecked = checkedSamples,
			MaximumAbsoluteTotalFluxKgPerSecond = maximumAbsoluteTotal,
			MaximumOutwardFaceFluxKgPerSecond = maximumOutwardFace,
		};
	}

	private static TimeValue[] ReadSeries(string root, string objectName)
	{
		string directory = Path.Combine(root, objectName);
		if (!Directory.Exists(directory))
			throw new InvalidDataException($"OpenFOAM did not produce the '{objectName}' patch-phi history.");
		List<TimeValue> values = [];
		foreach (string file in Directory.EnumerateFiles(directory, "*.dat", SearchOption.AllDirectories)
			.OrderBy(value => value, StringComparer.Ordinal))
		{
			foreach (string line in File.ReadLines(file))
			{
				string value = line.Trim();
				if (value.Length == 0 || value[0] == '#') continue;
				string[] columns = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
				if (columns.Length < 2
					|| !double.TryParse(columns[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double time)
					|| !double.TryParse(columns[^1], NumberStyles.Float, CultureInfo.InvariantCulture, out double sampleValue))
				{
					throw new InvalidDataException($"The '{objectName}' patch-phi history is malformed.");
				}
				values.Add(new(time, sampleValue));
			}
		}
		TimeValue[] result = values.GroupBy(value => value.Time).Select(value => value.Last())
			.OrderBy(value => value.Time).ToArray();
		if (result.Length < 2) throw new InvalidDataException($"The '{objectName}' patch-phi history is incomplete.");
		return result;
	}

	private static double Interpolate(IReadOnlyList<TimeValue> values, double time)
	{
		if (time < values[0].Time - 1e-12 || time > values[^1].Time + 1e-12)
			throw new InvalidDataException($"Patch-phi history does not cover time {time:R} s.");
		for (int index = 1; index < values.Count; ++index)
		{
			if (time > values[index].Time) continue;
			TimeValue a = values[index - 1];
			TimeValue b = values[index];
			if (Math.Abs(b.Time - a.Time) <= double.Epsilon) return b.Value;
			double amount = (time - a.Time) / (b.Time - a.Time);
			return a.Value + amount * (b.Value - a.Value);
		}
		return values[^1].Value;
	}

	private readonly record struct TimeValue(double Time, double Value);
}
