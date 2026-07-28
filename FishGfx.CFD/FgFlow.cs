using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FishGfx.CFD;

public sealed record CfdFlowFrameSource(
	int Index,
	double TimeSeconds,
	double CrankAngleDegrees,
	VerifiedOpenFoamResults Results);

public sealed record CfdFieldRange(double Minimum, double Maximum);

internal sealed record FgFlowBlock(long Offset, int CompressedSize, int UncompressedSize, string Sha256);
internal sealed record FgFlowFieldBlock(string Association, string Field, long Offset, int ComponentCount, int ValueCount);
internal sealed record FgFlowFrameHeader(
	int Index,
	double TimeSeconds,
	double CrankAngleDegrees,
	string VelocityBlockChecksum,
	FgFlowBlock Block,
	FgFlowFieldBlock[] Fields);
internal sealed record FgFlowHeader(
	string ByteOrder,
	string FloatRepresentation,
	int FormatVersion,
	string TopologyHash,
	string SolveHash,
	string CaptureHash,
	int AcceptedCycle,
	int FrameCount,
	FgFlowBlock Geometry,
	Dictionary<string, CfdFieldRange> Ranges,
	FgFlowFrameHeader[] Frames);

internal sealed record FgFlowDataSetGeometry(
	string Name,
	string Role,
	string Association,
	VtkVector[] Points,
	VtkCell[] Cells,
	int[] SourcePointIndices);

internal sealed record FgFlowTopology(FgFlowDataSetGeometry[] DataSets)
{
	internal static FgFlowTopology Create(VerifiedOpenFoamResults source, int maximumVolumeSamples)
	{
		int[] volumeIndices = SampleIndices(source.Volume.Points.Length, maximumVolumeSamples);
		List<FgFlowDataSetGeometry> dataSets =
		[
			new(
				"volume",
				"volume",
				"volume",
				volumeIndices.Select(index => source.Volume.Points[index]).ToArray(),
				[],
				volumeIndices),
		];
		foreach (CfdBoundaryPatch boundary in source.Boundaries.OrderBy(value => value.Name, StringComparer.Ordinal))
		{
			int[] indices = Enumerable.Range(0, boundary.Data.Points.Length).ToArray();
			dataSets.Add(new(
				boundary.Name,
				boundary.Role,
				boundary.Role == "walls" ? "walls" : "openings",
				boundary.Data.Points,
				boundary.Data.Cells,
				indices));
		}
		return new(dataSets.ToArray());
	}

	private static int[] SampleIndices(int count, int maximum)
	{
		if (count <= maximum) return Enumerable.Range(0, count).ToArray();
		return Enumerable.Range(0, maximum)
			.Select(index => checked((int)((long)index * count / maximum)))
			.ToArray();
	}
}

public static class FgFlowWriter
{
	private static readonly byte[] Magic = Encoding.ASCII.GetBytes("FGFLOW1\0");

	public static string Write(
		string path,
		string solveHash,
		string captureHash,
		int acceptedCycle,
		IReadOnlyList<CfdFlowFrameSource> frames,
		CfdResultStorageSettings storage)
		=> WriteStreaming(path, solveHash, captureHash, acceptedCycle, frames.Count, frames, storage);

	public static string WriteStreaming(
		string path,
		string solveHash,
		string captureHash,
		int acceptedCycle,
		int frameCount,
		IEnumerable<CfdFlowFrameSource> frames,
		CfdResultStorageSettings storage)
	{
		storage.Validate();
		if (frameCount < 1) throw new ArgumentException("FGFLOW requires at least one frame.", nameof(frameCount));
		string fullPath = Path.GetFullPath(path);
		Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
		string payloadPath = fullPath + $".{Guid.NewGuid():N}.payload";
		string temporary = fullPath + $".{Guid.NewGuid():N}.tmp";
		string backup = fullPath + $".{Guid.NewGuid():N}.bak";
		try
		{
			using IEnumerator<CfdFlowFrameSource> enumerator = frames.GetEnumerator();
			if (!enumerator.MoveNext() || enumerator.Current.Index != 0)
				throw new InvalidDataException("FGFLOW frame indices must be contiguous and zero-based.");
			CfdFlowFrameSource firstFrame = enumerator.Current;
			FgFlowTopology topology = FgFlowTopology.Create(firstFrame.Results, storage.MaximumVolumeSamples);
			byte[] topologyBytes = SerializeTopology(topology);
			string topologyHash = CfdJson.Hash(topologyBytes);
			Dictionary<string, (double Minimum, double Maximum)> ranges = new(StringComparer.Ordinal);
			List<FgFlowFrameHeader> frameHeaders = [];
			FgFlowBlock geometry;
			using (FileStream payload = new(payloadPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
			{
				geometry = WriteBlock(payload, topologyBytes, storage.CompressionQuality);
				int expectedIndex = 0;
				do
				{
					CfdFlowFrameSource frame = enumerator.Current;
					if (frame.Index != expectedIndex++)
						throw new InvalidDataException("FGFLOW frame indices must be contiguous and zero-based.");
					ValidateTopology(topology, frame.Results);
					(byte[] bytes, FgFlowFieldBlock[] fields, string velocityChecksum) =
						SerializeFrame(topology, frame.Results, ranges);
					FgFlowBlock block = WriteBlock(payload, bytes, storage.CompressionQuality);
					frameHeaders.Add(new(
						frame.Index,
						frame.TimeSeconds,
						frame.CrankAngleDegrees,
						velocityChecksum,
						block,
						fields));
				}
				while (enumerator.MoveNext());
				if (expectedIndex != frameCount)
					throw new InvalidDataException("FGFLOW frame enumeration did not match its declared count.");
				payload.Flush(true);
			}
			FgFlowHeader header = new(
				"little-endian",
				"IEEE-754-binary32",
				CfdResultStorageSettings.FormatVersion,
				topologyHash,
				solveHash,
				captureHash,
				acceptedCycle,
				frameCount,
				geometry,
				ranges.ToDictionary(
					value => value.Key,
					value => new CfdFieldRange(value.Value.Minimum, value.Value.Maximum),
					StringComparer.Ordinal),
				frameHeaders.ToArray());
			byte[] headerBytes = JsonSerializer.SerializeToUtf8Bytes(header, CfdJson.Options);
			using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
			{
				output.Write(Magic);
				using BinaryWriter writer = new(output, Encoding.UTF8, true);
				writer.Write(CfdResultStorageSettings.FormatVersion);
				writer.Write(headerBytes.Length);
				writer.Write(headerBytes);
				using FileStream payload = new(payloadPath, FileMode.Open, FileAccess.Read, FileShare.Read);
				payload.CopyTo(output);
				output.Flush(true);
			}
			using (FgFlowResultSequence validation = new(temporary))
			{
				if (validation.FrameCount != frameCount || validation.Header.TopologyHash != topologyHash)
					throw new InvalidDataException("The written FGFLOW file failed validation.");
			}
			if (File.Exists(fullPath))
			{
				File.Replace(temporary, fullPath, backup, true);
				File.Delete(backup);
			}
			else File.Move(temporary, fullPath);
			return HashFile(fullPath);
		}
		finally
		{
			File.Delete(payloadPath);
			File.Delete(temporary);
			File.Delete(backup);
		}
	}

	private static FgFlowBlock WriteBlock(Stream output, byte[] bytes, int quality)
	{
		long offset = output.Position;
		byte[] compressed = new byte[BrotliEncoder.GetMaxCompressedLength(bytes.Length)];
		if (!BrotliEncoder.TryCompress(bytes, compressed, out int written, quality, 22))
			throw new InvalidDataException("An FGFLOW block could not be compressed.");
		output.Write(compressed, 0, written);
		return new(offset, written, bytes.Length, CfdJson.Hash(bytes));
	}

	internal static string HashFile(string path)
	{
		using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024);
		return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
	}

	private static byte[] SerializeTopology(FgFlowTopology topology)
	{
		using MemoryStream output = new();
		using BinaryWriter writer = new(output, Encoding.UTF8, true);
		writer.Write(topology.DataSets.Length);
		foreach (FgFlowDataSetGeometry data in topology.DataSets)
		{
			writer.Write(data.Name);
			writer.Write(data.Role);
			writer.Write(data.Association);
			writer.Write(data.Points.Length);
			foreach (VtkVector point in data.Points)
			{
				writer.Write((float)point.X); writer.Write((float)point.Y); writer.Write((float)point.Z);
			}
			writer.Write(data.SourcePointIndices.Length);
			foreach (int index in data.SourcePointIndices) writer.Write(index);
			writer.Write(data.Cells.Length);
			foreach (VtkCell cell in data.Cells)
			{
				writer.Write(cell.Type);
				writer.Write(cell.PointIndices.Length);
				foreach (int index in cell.PointIndices) writer.Write(index);
			}
		}
		writer.Flush();
		return output.ToArray();
	}

	private static (byte[] Bytes, FgFlowFieldBlock[] Fields, string VelocityChecksum) SerializeFrame(
		FgFlowTopology topology,
		VerifiedOpenFoamResults source,
		Dictionary<string, (double Minimum, double Maximum)> ranges)
	{
		using MemoryStream output = new();
		using BinaryWriter writer = new(output, Encoding.UTF8, true);
		List<FgFlowFieldBlock> fields = [];
		string velocityChecksum = new string('0', 64);
		writer.Write(topology.DataSets.Length);
		foreach (FgFlowDataSetGeometry geometry in topology.DataSets)
		{
			LegacyVtkDataSet data = geometry.Role == "volume"
				? source.Volume
				: source.Boundaries.Single(value => value.Name == geometry.Name).Data;
			writer.Write(geometry.Name);
			string[] scalarNames = ScalarFields(geometry.Association).Where(data.PointScalars.ContainsKey).ToArray();
			writer.Write(scalarNames.Length);
			foreach (string name in scalarNames)
			{
				writer.Write(name);
				long offset = output.Position;
				writer.Write(geometry.SourcePointIndices.Length);
				double[] values = data.PointScalars[name];
				foreach (int index in geometry.SourcePointIndices) writer.Write((float)values[index]);
				fields.Add(new(geometry.Association, name, offset, 1, geometry.SourcePointIndices.Length));
				UpdateRange(ranges, $"{name}/{geometry.Association}", geometry.SourcePointIndices.Select(index => values[index]));
			}
			string[] vectorNames = data.PointVectors.ContainsKey("U") ? ["U"] : [];
			writer.Write(vectorNames.Length);
			foreach (string name in vectorNames)
			{
				writer.Write(name);
				long offset = output.Position;
				writer.Write(geometry.SourcePointIndices.Length);
				VtkVector[] values = data.PointVectors[name];
				using MemoryStream velocityBytes = new();
				using BinaryWriter velocityWriter = new(velocityBytes);
				List<double> magnitudes = [];
				foreach (int index in geometry.SourcePointIndices)
				{
					VtkVector value = values[index];
					writer.Write((float)value.X); writer.Write((float)value.Y); writer.Write((float)value.Z);
					velocityWriter.Write((float)value.X); velocityWriter.Write((float)value.Y); velocityWriter.Write((float)value.Z);
					magnitudes.Add(value.Length);
				}
				fields.Add(new(geometry.Association, name, offset, 3, geometry.SourcePointIndices.Length));
				UpdateRange(ranges, $"{name}/{geometry.Association}", magnitudes);
				if (geometry.Association == "volume") velocityChecksum = CfdJson.Hash(velocityBytes.ToArray());
			}
		}
		writer.Flush();
		return (output.ToArray(), fields.ToArray(), velocityChecksum);
	}

	private static IEnumerable<string> ScalarFields(string role) => role switch
	{
		"volume" => ["p", "T", "rho", "Ma"],
		"walls" => ["p", "T", "rho", "Ma", "yPlus"],
		_ => ["p", "T", "rho", "Ma"],
	};

	private static void UpdateRange(
		Dictionary<string, (double Minimum, double Maximum)> ranges,
		string key,
		IEnumerable<double> values)
	{
		double[] finite = values.Where(double.IsFinite).ToArray();
		if (finite.Length == 0) return;
		double minimum = finite.Min();
		double maximum = finite.Max();
		if (ranges.TryGetValue(key, out var previous))
			ranges[key] = (Math.Min(previous.Minimum, minimum), Math.Max(previous.Maximum, maximum));
		else ranges[key] = (minimum, maximum);
	}

	private static void ValidateTopology(FgFlowTopology topology, VerifiedOpenFoamResults source)
	{
		foreach (FgFlowDataSetGeometry geometry in topology.DataSets)
		{
			LegacyVtkDataSet data = geometry.Role == "volume"
				? source.Volume
				: source.Boundaries.Single(value => value.Name == geometry.Name).Data;
			if (geometry.SourcePointIndices.Any(index => index >= data.Points.Length))
				throw new InvalidDataException("FGFLOW frame topology changed between frames.");
			for (int point = 0; point < geometry.SourcePointIndices.Length; ++point)
			{
				VtkVector expected = geometry.Points[point];
				VtkVector actual = data.Points[geometry.SourcePointIndices[point]];
				if ((expected - actual).Length > 1e-5)
					throw new InvalidDataException("FGFLOW point positions changed between frames.");
			}
		}
	}
}

public sealed class FgFlowResultSequence : ICfdResultSequence, ICfdFieldRangeProvider, IDisposable
{
	private static readonly byte[] Magic = Encoding.ASCII.GetBytes("FGFLOW1\0");
	private readonly string path;
	private readonly long payloadStart;
	private readonly FgFlowTopology topology;
	private readonly CfdThreeFrameCache cache = new();
	private readonly SemaphoreSlim gate = new(1, 1);
	private bool disposed;

	internal FgFlowHeader Header { get; }
	public CfdAnalysisMode AnalysisMode => CfdAnalysisMode.EngineTransient;
	public int FrameCount => Header.FrameCount;
	public IReadOnlyDictionary<string, CfdFieldRange> Ranges => Header.Ranges;
	public bool TryGetRange(string field, string association, out CfdFieldRange range) =>
		Header.Ranges.TryGetValue($"{field}/{association}", out range!);

	public FgFlowResultSequence(string path, string? expectedSha256 = null)
	{
		this.path = Path.GetFullPath(path);
		if (expectedSha256 != null
			&& !string.Equals(FgFlowWriter.HashFile(this.path), expectedSha256, StringComparison.OrdinalIgnoreCase))
			throw new InvalidDataException("The FGFLOW file hash does not match the case reference.");
		using FileStream input = new(this.path, FileMode.Open, FileAccess.Read, FileShare.Read);
		byte[] magic = new byte[Magic.Length];
		input.ReadExactly(magic);
		if (!magic.SequenceEqual(Magic)) throw new InvalidDataException("The result is not an FGFLOW file.");
		using BinaryReader reader = new(input, Encoding.UTF8, true);
		int version = reader.ReadInt32();
		int headerLength = reader.ReadInt32();
		if (version != CfdResultStorageSettings.FormatVersion || headerLength <= 0)
			throw new InvalidDataException("The FGFLOW format version is unsupported.");
		Header = JsonSerializer.Deserialize<FgFlowHeader>(reader.ReadBytes(headerLength), CfdJson.Options)
			?? throw new InvalidDataException("The FGFLOW header is missing.");
		if (Header.ByteOrder != "little-endian" || Header.FloatRepresentation != "IEEE-754-binary32"
			|| Header.FrameCount != Header.Frames.Length)
			throw new InvalidDataException("The FGFLOW header is incompatible.");
		payloadStart = input.Position;
		topology = DeserializeTopology(ReadBlock(input, Header.Geometry));
		if (CfdJson.Hash(SerializeTopologyForValidation(topology)) != Header.TopologyHash)
			throw new InvalidDataException("The FGFLOW topology checksum is invalid.");
	}

	public CfdFrameInfo GetFrameInfo(int index)
	{
		FgFlowFrameHeader frame = Header.Frames.ElementAtOrDefault(index)
			?? throw new ArgumentOutOfRangeException(nameof(index));
		return new(frame.Index, frame.TimeSeconds, frame.CrankAngleDegrees, frame.VelocityBlockChecksum);
	}

	public async ValueTask<CfdResultFrame> LoadFrameAsync(int index, CancellationToken cancellationToken)
	{
		if ((uint)index >= (uint)FrameCount) throw new ArgumentOutOfRangeException(nameof(index));
		await gate.WaitAsync(cancellationToken);
		try
		{
			if (cache.TryGet(index, out CfdResultFrame? cached)) return cached!;
			FgFlowFrameHeader header = Header.Frames[index];
			byte[] bytes;
			await using (FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true))
			{
				bytes = await ReadBlockAsync(input, header.Block, cancellationToken);
			}
			CfdResultFrame frame = DeserializeFrame(header, bytes);
			cache.Add(frame);
			return frame;
		}
		finally { gate.Release(); }
	}

	private CfdResultFrame DeserializeFrame(FgFlowFrameHeader header, byte[] bytes)
	{
		using MemoryStream input = new(bytes, false);
		using BinaryReader reader = new(input, Encoding.UTF8, true);
		int dataSetCount = reader.ReadInt32();
		if (dataSetCount != topology.DataSets.Length) throw new InvalidDataException("FGFLOW dataset count changed.");
		LegacyVtkDataSet? volume = null;
		List<CfdBoundaryPatch> boundaries = [];
		foreach (FgFlowDataSetGeometry geometry in topology.DataSets)
		{
			string name = reader.ReadString();
			if (name != geometry.Name) throw new InvalidDataException("FGFLOW dataset order changed.");
			LegacyVtkDataSet data = new() { Points = geometry.Points, Cells = geometry.Cells };
			int scalars = reader.ReadInt32();
			for (int field = 0; field < scalars; ++field)
			{
				string fieldName = reader.ReadString();
				int count = reader.ReadInt32();
				double[] values = new double[count];
				for (int index = 0; index < count; ++index) values[index] = reader.ReadSingle();
				data.PointScalars[fieldName] = values;
			}
			int vectors = reader.ReadInt32();
			for (int field = 0; field < vectors; ++field)
			{
				string fieldName = reader.ReadString();
				int count = reader.ReadInt32();
				VtkVector[] values = new VtkVector[count];
				for (int index = 0; index < count; ++index)
					values[index] = new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
				data.PointVectors[fieldName] = values;
			}
			if (geometry.Role == "volume") volume = data;
			else boundaries.Add(new(geometry.Name, geometry.Role, data));
		}
		return new(
			new(header.Index, header.TimeSeconds, header.CrankAngleDegrees, header.VelocityBlockChecksum),
			new(volume ?? throw new InvalidDataException("FGFLOW has no volume dataset."), boundaries));
	}

	private byte[] ReadBlock(Stream input, FgFlowBlock block)
	{
		input.Position = payloadStart + block.Offset;
		byte[] compressed = new byte[block.CompressedSize];
		input.ReadExactly(compressed);
		return DecompressAndValidate(compressed, block);
	}

	private async Task<byte[]> ReadBlockAsync(FileStream input, FgFlowBlock block, CancellationToken cancellationToken)
	{
		input.Position = payloadStart + block.Offset;
		byte[] compressed = new byte[block.CompressedSize];
		await input.ReadExactlyAsync(compressed, cancellationToken);
		return DecompressAndValidate(compressed, block);
	}

	private static byte[] DecompressAndValidate(byte[] compressed, FgFlowBlock block)
	{
		using BrotliStream brotli = new(new MemoryStream(compressed, false), CompressionMode.Decompress);
		using MemoryStream output = new(block.UncompressedSize);
		brotli.CopyTo(output);
		byte[] result = output.ToArray();
		if (result.Length != block.UncompressedSize || CfdJson.Hash(result) != block.Sha256)
			throw new InvalidDataException("An FGFLOW block checksum is invalid.");
		return result;
	}

	private static FgFlowTopology DeserializeTopology(byte[] bytes)
	{
		using BinaryReader reader = new(new MemoryStream(bytes, false), Encoding.UTF8);
		int count = reader.ReadInt32();
		FgFlowDataSetGeometry[] dataSets = new FgFlowDataSetGeometry[count];
		for (int dataSet = 0; dataSet < count; ++dataSet)
		{
			string name = reader.ReadString();
			string role = reader.ReadString();
			string association = reader.ReadString();
			VtkVector[] points = new VtkVector[reader.ReadInt32()];
			for (int index = 0; index < points.Length; ++index)
				points[index] = new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
			int[] sourceIndices = new int[reader.ReadInt32()];
			for (int index = 0; index < sourceIndices.Length; ++index) sourceIndices[index] = reader.ReadInt32();
			VtkCell[] cells = new VtkCell[reader.ReadInt32()];
			for (int index = 0; index < cells.Length; ++index)
			{
				int type = reader.ReadInt32();
				int[] indices = new int[reader.ReadInt32()];
				for (int point = 0; point < indices.Length; ++point) indices[point] = reader.ReadInt32();
				cells[index] = new(type, indices);
			}
			dataSets[dataSet] = new(name, role, association, points, cells, sourceIndices);
		}
		return new(dataSets);
	}

	private static byte[] SerializeTopologyForValidation(FgFlowTopology topology)
	{
		using MemoryStream output = new();
		using BinaryWriter writer = new(output, Encoding.UTF8, true);
		writer.Write(topology.DataSets.Length);
		foreach (FgFlowDataSetGeometry data in topology.DataSets)
		{
			writer.Write(data.Name); writer.Write(data.Role); writer.Write(data.Association); writer.Write(data.Points.Length);
			foreach (VtkVector point in data.Points)
			{
				writer.Write((float)point.X); writer.Write((float)point.Y); writer.Write((float)point.Z);
			}
			writer.Write(data.SourcePointIndices.Length);
			foreach (int value in data.SourcePointIndices) writer.Write(value);
			writer.Write(data.Cells.Length);
			foreach (VtkCell cell in data.Cells)
			{
				writer.Write(cell.Type); writer.Write(cell.PointIndices.Length);
				foreach (int value in cell.PointIndices) writer.Write(value);
			}
		}
		writer.Flush();
		return output.ToArray();
	}

	public void Dispose()
	{
		if (disposed) return;
		disposed = true;
		gate.Dispose();
	}
}
