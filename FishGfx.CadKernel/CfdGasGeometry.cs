#nullable enable

using System.Runtime.InteropServices;
using System.Text;

namespace FishGfx.Cad;

public enum CadCfdGasPathKind
{
	Runner,
	Collector,
}

public enum CadCfdOpeningRole
{
	Inlet,
	Outlet,
}

public sealed record CadCfdGasPathInfo(
	string Id,
	string Name,
	string ComponentName,
	CadCfdGasPathKind Kind);

public sealed record CadCfdOpeningFingerprint(
	double Area,
	CadPoint3 Centroid,
	CadPoint3 Normal,
	int LoopCount,
	double Perimeter,
	IReadOnlyList<double> EdgeLengths,
	IReadOnlyList<CadPoint3> LoopSamples);

public sealed record CadCfdOpeningSpec(
	string Id,
	string PatchName,
	CadCfdOpeningRole Role,
	CadCfdOpeningFingerprint Fingerprint);

public sealed record CadCfdMatchResult(
	string OpeningId,
	string? SelectedCandidate,
	double BestScore,
	double SecondBestScore,
	uint FailedToleranceMask);

public sealed record CadCfdGeometryPreparation(
	CadPoint3 MinimumMm,
	CadPoint3 MaximumMm,
	CadPoint3 InteriorPointMm,
	double SmallestInletHydraulicDiameterMm,
	IReadOnlyList<CadCfdMatchResult> Matches);

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeCfdPathInfo
{
	internal fixed byte Id[40];
	internal fixed byte Name[128];
	internal fixed byte ComponentName[256];
	internal CadCfdGasPathKind Kind;
}

[StructLayout(LayoutKind.Sequential)]
internal readonly record struct NativeCfdMatchingPolicy(
	uint Version,
	double AreaAbsoluteTolerance,
	double AreaRelativeTolerance,
	double CentroidToleranceMm,
	double NormalAngularToleranceDegrees,
	double PerimeterRelativeTolerance,
	double LoopSampleToleranceMm,
	double UniqueScoreMargin)
{
	internal NativeCfdMatchingPolicy(CadPatchMatchingPolicy policy)
		: this(
			(uint)policy.Version,
			policy.AreaAbsoluteTolerance,
			policy.AreaRelativeTolerance,
			policy.CentroidToleranceMm,
			policy.NormalAngularToleranceDegrees,
			policy.PerimeterRelativeTolerance,
			policy.LoopSampleToleranceMm,
			policy.UniqueScoreMargin)
	{
	}
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeCfdOpeningSpec
{
	internal fixed byte Id[128];
	internal fixed byte PatchName[128];
	internal CadCfdOpeningRole Role;
	internal double Area;
	internal NativePoint3 Centroid;
	internal NativePoint3 Normal;
	internal uint LoopCount;
	internal double Perimeter;
	internal uint EdgeLengthCount;
	internal fixed double EdgeLengths[64];
	internal uint LoopSampleCount;
	internal fixed double LoopSamples[64 * 3];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeCfdMatchResult
{
	internal fixed byte OpeningId[128];
	internal fixed byte SelectedCandidate[128];
	internal double BestScore;
	internal double SecondBestScore;
	internal uint FailedToleranceMask;
}

[StructLayout(LayoutKind.Sequential)]
internal readonly record struct NativeCfdGeometryInfo(
	NativePoint3 MinimumMm,
	NativePoint3 MaximumMm,
	NativePoint3 InteriorPointMm,
	double SmallestInletHydraulicDiameterMm);

public sealed class CfdGasGeometry : IDisposable
{
	private readonly CfdGeometrySafeHandle handle;
	private bool disposed;

	private CfdGasGeometry(CfdGeometrySafeHandle handle)
	{
		this.handle = handle;
	}

	public static CfdGasGeometry ImportStep(string path)
	{
		CadDocument.Check(
			NativeMethods.CfdGeometryImportStep(Path.GetFullPath(path), out nint value),
			"Import CFD gas STEP");
		return new(new CfdGeometrySafeHandle(value));
	}

	public unsafe IReadOnlyList<CadCfdGasPathInfo> GetPaths()
	{
		ThrowIfDisposed();
		CadDocument.Check(NativeMethods.CfdGeometryGetPathCount(handle, out nuint count), "Get CFD gas paths");
		NativeCfdPathInfo[] native = new NativeCfdPathInfo[checked((int)count)];
		fixed (NativeCfdPathInfo* pointer = native)
		{
			CadDocument.Check(
				NativeMethods.CfdGeometryCopyPaths(handle, pointer, count),
				"Copy CFD gas paths");
		}
		List<CadCfdGasPathInfo> result = new(native.Length);
		for (int index = 0; index < native.Length; ++index)
		{
			NativeCfdPathInfo item = native[index];
			byte* id = item.Id;
			byte* name = item.Name;
			byte* component = item.ComponentName;
			result.Add(new CadCfdGasPathInfo(
				Text(id, 40),
				Text(name, 128),
				Text(component, 256),
				item.Kind));
		}
		return result;
	}

	public unsafe CadCfdGeometryPreparation PreparePath(
		string pathId,
		IReadOnlyList<CadCfdOpeningSpec> openings,
		CadPatchMatchingPolicy? policy = null)
	{
		ThrowIfDisposed();
		if (openings.Count == 0) throw new ArgumentException("At least one CFD opening is required.", nameof(openings));
		policy ??= CadPatchMatchingPolicy.Version1;
		NativeCfdOpeningSpec[] nativeOpenings = openings.Select(ToNative).ToArray();
		NativeCfdMatchResult[] nativeResults = new NativeCfdMatchResult[openings.Count];
		NativeCfdMatchingPolicy nativePolicy = new(policy);
		fixed (NativeCfdOpeningSpec* openingPointer = nativeOpenings)
		fixed (NativeCfdMatchResult* resultPointer = nativeResults)
		{
			CadDocument.Check(NativeMethods.CfdGeometryPreparePath(
				handle,
				pathId,
				openingPointer,
				(nuint)nativeOpenings.Length,
				in nativePolicy,
				resultPointer,
				(nuint)nativeResults.Length,
				out NativeCfdGeometryInfo info), "Match CFD gas openings");
			return new CadCfdGeometryPreparation(
				info.MinimumMm.ToManaged(),
				info.MaximumMm.ToManaged(),
				info.InteriorPointMm.ToManaged(),
				info.SmallestInletHydraulicDiameterMm,
				nativeResults.Select(ToManaged).ToArray());
		}
	}

	public CadTessellation Tessellate(
		double linearDeflection = 0.25,
		double angularDeflection = Math.PI / 18)
	{
		ThrowIfDisposed();
		CadDocument.Check(
			NativeMethods.CfdGeometryTessellate(handle, linearDeflection, angularDeflection, out nint value),
			"Tessellate CFD gas path");
		using CadTessellationSafeHandle tessellation = new(value);
		return CadDocument.CopyTessellation(tessellation);
	}

	public void ExportMultiRegionStl(string path)
	{
		ThrowIfDisposed();
		CadDocument.Check(
			NativeMethods.CfdGeometryExportMultiRegionStl(handle, Path.GetFullPath(path)),
			"Export CFD multi-region STL");
	}

	public void Dispose()
	{
		if (disposed) return;
		disposed = true;
		handle.Dispose();
	}

	private static unsafe NativeCfdOpeningSpec ToNative(CadCfdOpeningSpec source)
	{
		NativeCfdOpeningSpec result = default;
		byte* id = result.Id;
		byte* patch = result.PatchName;
		CopyText(id, 128, source.Id);
		CopyText(patch, 128, source.PatchName);
		result.Role = source.Role;
		result.Area = source.Fingerprint.Area;
		result.Centroid = new NativePoint3(source.Fingerprint.Centroid);
		result.Normal = new NativePoint3(source.Fingerprint.Normal);
		result.LoopCount = checked((uint)source.Fingerprint.LoopCount);
		result.Perimeter = source.Fingerprint.Perimeter;
		result.EdgeLengthCount = checked((uint)Math.Min(source.Fingerprint.EdgeLengths.Count, 64));
		double* edges = result.EdgeLengths;
		for (int index = 0; index < result.EdgeLengthCount; ++index)
		{
			edges[index] = source.Fingerprint.EdgeLengths[index];
		}
		result.LoopSampleCount = checked((uint)Math.Min(source.Fingerprint.LoopSamples.Count, 64));
		double* samples = result.LoopSamples;
		for (int index = 0; index < result.LoopSampleCount; ++index)
		{
			CadPoint3 point = source.Fingerprint.LoopSamples[index];
			samples[index * 3] = point.X;
			samples[index * 3 + 1] = point.Y;
			samples[index * 3 + 2] = point.Z;
		}
		return result;
	}

	private static unsafe CadCfdMatchResult ToManaged(NativeCfdMatchResult source)
	{
		byte* opening = source.OpeningId;
		byte* selected = source.SelectedCandidate;
		string candidate = Text(selected, 128);
		return new(
			Text(opening, 128),
			candidate.Length == 0 ? null : candidate,
			source.BestScore,
			source.SecondBestScore,
			source.FailedToleranceMask);
	}

	private static unsafe void CopyText(byte* destination, int capacity, string value)
	{
		byte[] bytes = Encoding.UTF8.GetBytes(value);
		for (int index = 0; index < Math.Min(bytes.Length, capacity - 1); ++index) destination[index] = bytes[index];
	}

	private static unsafe string Text(byte* value, int capacity)
	{
		int length = 0;
		while (length < capacity && value[length] != 0) ++length;
		return Encoding.UTF8.GetString(value, length);
	}

	private void ThrowIfDisposed()
	{
		ObjectDisposedException.ThrowIf(disposed, this);
	}
}
