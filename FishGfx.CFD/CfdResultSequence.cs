namespace FishGfx.CFD;

public sealed record CfdFrameInfo(
	int Index,
	double TimeSeconds,
	double CrankAngleDegrees,
	string VelocityBlockChecksum);

public sealed record CfdResultFrame(
	CfdFrameInfo Info,
	VerifiedOpenFoamResults Results);

public interface ICfdResultSequence
{
	CfdAnalysisMode AnalysisMode { get; }
	int FrameCount { get; }
	CfdFrameInfo GetFrameInfo(int index);
	ValueTask<CfdResultFrame> LoadFrameAsync(int index, CancellationToken cancellationToken);
}

public interface ICfdFieldRangeProvider
{
	bool TryGetRange(string field, string association, out CfdFieldRange range);
}

public sealed class CfdSteadyResultSequence : ICfdResultSequence
{
	private readonly CfdResultFrame frame;

	public CfdSteadyResultSequence(VerifiedOpenFoamResults results)
	{
		frame = new(new(0, 0, 0, SteadyVelocityChecksum(results.Volume)), results);
	}

	public CfdAnalysisMode AnalysisMode => CfdAnalysisMode.Steady;
	public int FrameCount => 1;
	public CfdFrameInfo GetFrameInfo(int index) => index == 0
		? frame.Info
		: throw new ArgumentOutOfRangeException(nameof(index));
	public ValueTask<CfdResultFrame> LoadFrameAsync(int index, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		return ValueTask.FromResult(index == 0
			? frame
			: throw new ArgumentOutOfRangeException(nameof(index)));
	}

	private static string SteadyVelocityChecksum(LegacyVtkDataSet volume)
	{
		if (!volume.PointVectors.TryGetValue("U", out VtkVector[]? values)) return new string('0', 64);
		using MemoryStream bytes = new();
		using BinaryWriter writer = new(bytes);
		foreach (VtkVector value in values)
		{
			writer.Write(value.X);
			writer.Write(value.Y);
			writer.Write(value.Z);
		}
		writer.Flush();
		return CfdJson.Hash(bytes.ToArray());
	}
}

internal sealed class CfdThreeFrameCache
{
	private readonly Dictionary<int, CfdResultFrame> frames = [];
	private readonly LinkedList<int> order = [];

	internal bool TryGet(int index, out CfdResultFrame? frame)
	{
		if (!frames.TryGetValue(index, out frame)) return false;
		order.Remove(index);
		order.AddLast(index);
		return true;
	}

	internal void Add(CfdResultFrame frame)
	{
		frames[frame.Info.Index] = frame;
		order.Remove(frame.Info.Index);
		order.AddLast(frame.Info.Index);
		while (order.Count > 3)
		{
			int remove = order.First!.Value;
			order.RemoveFirst();
			frames.Remove(remove);
		}
	}
}
