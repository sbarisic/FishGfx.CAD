using System.Globalization;

namespace FishGfx.CFD;

public static class OpenFoamTransientMonitor
{
	public static CfdPeriodicityResult ReadAndCompareLastCycles(
		string resultDirectory,
		CfdEngineTransientSettings settings)
		=> ReadAndCompareCycle(resultDirectory, settings, settings.MaximumCycles);

	public static CfdPeriodicityResult ReadAndCompareCycle(
		string resultDirectory,
		CfdEngineTransientSettings settings,
		int currentCycle)
	{
		string root = Path.Combine(resultDirectory, "postProcessing");
		TimeValue[] flow = ReadSeries(root, "outletMassFlow");
		TimeValue[] pressure = ReadSeries(root, "outletPressure");
		TimeValue[] mass = ReadSeries(root, "domainMass");
		if (currentCycle < settings.MinimumCycles || currentCycle > settings.MaximumCycles)
			throw new ArgumentOutOfRangeException(nameof(currentCycle));
		CfdCycleMonitorSample[] previous = SampleCycle(
			currentCycle - 1, settings, flow, pressure, mass);
		CfdCycleMonitorSample[] current = SampleCycle(
			currentCycle, settings, flow, pressure, mass);
		return CfdTransientPeriodicity.Compare(currentCycle, previous, current, settings);
	}

	private static CfdCycleMonitorSample[] SampleCycle(
		int oneBasedCycle,
		CfdEngineTransientSettings settings,
		TimeValue[] flow,
		TimeValue[] pressure,
		TimeValue[] mass)
	{
		if (oneBasedCycle < 1) throw new InvalidDataException("At least two transient cycles are required for comparison.");
		int count = checked((int)Math.Round(720.0 / settings.SolverAlignmentDegrees));
		double cycleStart = (oneBasedCycle - 1) * settings.CycleDurationSeconds;
		CfdCycleMonitorSample[] samples = new CfdCycleMonitorSample[count];
		for (int index = 0; index < count; ++index)
		{
			double angle = index * settings.SolverAlignmentDegrees;
			double time = cycleStart + angle * settings.SecondsPerDegree;
			samples[index] = new(
				angle,
				Interpolate(flow, time, "outlet mass flow"),
				Interpolate(pressure, time, "outlet pressure"),
				Interpolate(mass, time, "domain mass"));
		}
		return samples;
	}

	private static TimeValue[] ReadSeries(string root, string objectName)
	{
		if (!Directory.Exists(root))
			throw new InvalidDataException("OpenFOAM did not produce transient monitoring histories.");
		string objectRoot = Path.Combine(root, objectName);
		if (!Directory.Exists(objectRoot))
			throw new InvalidDataException($"OpenFOAM did not produce the '{objectName}' history.");
		List<TimeValue> values = [];
		foreach (string file in Directory.EnumerateFiles(objectRoot, "*.dat", SearchOption.AllDirectories)
			.OrderBy(value => value, StringComparer.Ordinal))
		{
			foreach (string line in File.ReadLines(file))
			{
				string value = line.Trim();
				if (value.Length == 0 || value[0] == '#') continue;
				string[] columns = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
				if (columns.Length < 2
					|| !double.TryParse(columns[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double time)
					|| !double.TryParse(columns[^1], NumberStyles.Float, CultureInfo.InvariantCulture, out double sample)
					|| !double.IsFinite(time) || !double.IsFinite(sample))
				{
					throw new InvalidDataException($"The '{objectName}' history contains malformed data.");
				}
				values.Add(new(time, sample));
			}
		}
		TimeValue[] result = values
			.GroupBy(value => value.Time)
			.Select(group => group.Last())
			.OrderBy(value => value.Time)
			.ToArray();
		if (result.Length < 2)
			throw new InvalidDataException($"The '{objectName}' history is incomplete.");
		return result;
	}

	private static double Interpolate(TimeValue[] values, double time, string name)
	{
		const double tolerance = 1e-12;
		if (time < values[0].Time - tolerance || time > values[^1].Time + tolerance)
			throw new InvalidDataException($"The {name} history does not cover time {time:R} s.");
		int index = Array.BinarySearch(values, new TimeValue(time, 0), TimeComparer.Instance);
		if (index >= 0) return values[index].Value;
		int right = ~index;
		if (right == 0) return values[0].Value;
		if (right >= values.Length) return values[^1].Value;
		TimeValue a = values[right - 1];
		TimeValue b = values[right];
		double amount = (time - a.Time) / (b.Time - a.Time);
		return a.Value + (b.Value - a.Value) * amount;
	}

	private readonly record struct TimeValue(double Time, double Value);

	private sealed class TimeComparer : IComparer<TimeValue>
	{
		public static TimeComparer Instance { get; } = new();
		public int Compare(TimeValue x, TimeValue y) => x.Time.CompareTo(y.Time);
	}
}
