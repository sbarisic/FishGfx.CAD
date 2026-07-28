namespace FishGfx.Cad;

public enum CollectorLayoutPreset
{
	Row,
	Radial,
	Staggered,
}

public enum CadGenerationOwnerKind
{
	Runner,
	CollectorSystem,
}

public readonly record struct CadGenerationStamp(
	CadGenerationOwnerKind OwnerKind,
	Guid OwnerId,
	long Revision
);

public sealed class CadCollectorBinding
{
	public Guid RunnerId { get; set; }

	public Guid TerminalBezierNodeId { get; set; }

	public Guid? ClockingTransitionNodeId { get; set; }

	internal CadCollectorBinding DeepClone()
	{
		return new CadCollectorBinding
		{
			RunnerId = RunnerId,
			TerminalBezierNodeId = TerminalBezierNodeId,
			ClockingTransitionNodeId = ClockingTransitionNodeId,
		};
	}
}

public sealed class CadCollectorInlet
{
	public Guid Id { get; set; } = Guid.NewGuid();

	public string Name { get; set; } = "Inlet";

	public CadFrame LocalFrame { get; set; } = CollectorFrameDefaults.Inlet;

	public double MergeStation { get; set; } = 0.5;

	// Preferred start-handle length. The solved path may adjust it when the
	// requested outlet pose otherwise violates the active tube bend radius.
	public double BranchStartHandleLength { get; set; } = 35;

	public double BranchOuterRadiusMillimetres { get; set; } = 21.2;

	public CadCollectorBranchPath BranchPath { get; set; }

	public double ClockingTransitionLength { get; set; } = 20;

	public CadCollectorBinding Binding { get; set; }

	internal CadCollectorInlet DeepClone()
	{
		return new CadCollectorInlet
		{
			Id = Id,
			Name = Name,
			LocalFrame = LocalFrame,
			MergeStation = MergeStation,
			BranchStartHandleLength = BranchStartHandleLength,
			BranchOuterRadiusMillimetres = BranchOuterRadiusMillimetres,
			BranchPath = BranchPath?.DeepClone(),
			ClockingTransitionLength = ClockingTransitionLength,
			Binding = Binding?.DeepClone(),
		};
	}
}

public sealed class CadCollectorSystem
{
	public const double OutletTransitionSetbackDiameterRatio = 0.40;

	private long generationRevision;

	public Guid Id { get; set; } = Guid.NewGuid();

	public string Name { get; set; } = "Collector";

	public CadFrame OutletFrame { get; set; } = CollectorFrameDefaults.Outlet;

	// The exact collector lofts its outer and gas cut outlines directly to an
	// area-preserving circular outlet derived from all member gas profiles. This
	// value retains the derived diameter and supplies the outlet wall thickness.
	// Legacy stub and merge values remain serialized for graph.json compatibility
	// but do not create an outlet pipe.
	public PipeProfile OutletProfile { get; set; } = new(63.5, 2);

	public double OutletStubLength { get; set; } = 50;

	public double MergeLength { get; set; } = 100;

	public double OverlapLength { get; set; } = 12;

	public double OutletTransitionSetback { get; set; } =
		63.5 * OutletTransitionSetbackDiameterRatio;

	// Preferred shared end-handle length. Each solved branch may adjust it to
	// preserve its endpoint frame and bend-radius clearance.
	public double BranchEndHandleLength { get; set; } = 35;

	public List<CadCollectorInlet> Inlets { get; set; } = new();

	public long GenerationRevision => Interlocked.Read(ref generationRevision);

	public static double DefaultOutletTransitionSetback(PipeProfile outletProfile)
	{
		return outletProfile.OuterDiameterMillimetres
			* OutletTransitionSetbackDiameterRatio;
	}

	public static PipeProfile AreaPreservingOutletProfile(
		IEnumerable<RunnerSectionProfile> memberProfiles,
		double wallThicknessMillimetres
	)
	{
		ArgumentNullException.ThrowIfNull(memberProfiles);
		if (!double.IsFinite(wallThicknessMillimetres)
			|| wallThicknessMillimetres <= 0)
		{
			throw new ArgumentOutOfRangeException(
				nameof(wallThicknessMillimetres),
				"The collector outlet wall thickness must be finite and positive."
			);
		}

		RunnerSectionProfile[] profiles = memberProfiles.ToArray();
		if (profiles.Length < 2 || profiles.Any(profile => profile == null))
		{
			throw new ArgumentException(
				"An area-preserving collector outlet requires at least two member profiles.",
				nameof(memberProfiles)
			);
		}
		double innerArea = profiles.Sum(profile => profile.InnerAreaMillimetresSquared);
		if (!double.IsFinite(innerArea) || innerArea <= 0)
		{
			throw new ArgumentException(
				"The combined collector member gas area must be finite and positive.",
				nameof(memberProfiles)
			);
		}

		double innerRadius = Math.Sqrt(innerArea / Math.PI);
		return new PipeProfile(
			2 * (innerRadius + wallThicknessMillimetres),
			wallThicknessMillimetres
		);
	}

	public bool IsResolved { get; set; } = true;

	public string Diagnostic { get; set; }

	public CadExactBuildState ExactBuild { get; } = new();

	public CadGenerationStamp GenerationStamp =>
		new(CadGenerationOwnerKind.CollectorSystem, Id, GenerationRevision);

	public CadFrame GetWorldInletFrame(CadCollectorInlet inlet)
	{
		ArgumentNullException.ThrowIfNull(inlet);
		return OutletFrame.Compose(inlet.LocalFrame);
	}

	public void SetOutletFramePreservingWorldInlets(CadFrame outletFrame)
	{
		(CadCollectorInlet Inlet, CadFrame WorldFrame)[] inletFrames = Inlets
			.Select(inlet => (inlet, GetWorldInletFrame(inlet)))
			.ToArray();
		Dictionary<Guid, CadCollectorBranchPath> solvedPaths = inletFrames.ToDictionary(
			item => item.Inlet.Id,
			item => CadCollectorBranchSolver.Solve(
				item.WorldFrame,
				outletFrame,
				item.Inlet.BranchOuterRadiusMillimetres,
				item.Inlet.BranchStartHandleLength,
				BranchEndHandleLength,
				item.Inlet.BranchPath
			)
		);
		SetOutletFramePreservingWorldInlets(outletFrame, solvedPaths);
	}

	internal void SetOutletFramePreservingWorldInlets(
		CadFrame outletFrame,
		IReadOnlyDictionary<Guid, CadCollectorBranchPath> solvedPaths
	)
	{
		ArgumentNullException.ThrowIfNull(solvedPaths);
		(CadCollectorInlet Inlet, CadFrame WorldFrame)[] inletFrames = Inlets
			.Select(inlet => (inlet, GetWorldInletFrame(inlet)))
			.ToArray();
		CadCollectorBranchPath[] stagedPaths = new CadCollectorBranchPath[inletFrames.Length];
		for (int index = 0; index < inletFrames.Length; ++index)
		{
			(CadCollectorInlet inlet, CadFrame worldFrame) = inletFrames[index];
			if (!solvedPaths.TryGetValue(inlet.Id, out CadCollectorBranchPath path))
			{
				throw new InvalidOperationException(
					$"Collector inlet '{inlet.Name}' has no staged branch path."
				);
			}
			if (!CadCollectorBranchSolver.ValidatePath(
				path,
				outletFrame,
				worldFrame,
				out string error
			))
			{
				throw new InvalidOperationException($"{inlet.Name}: {error}");
			}
			stagedPaths[index] = path.DeepClone();
		}

		OutletFrame = outletFrame;
		for (int index = 0; index < inletFrames.Length; ++index)
		{
			(CadCollectorInlet inlet, CadFrame worldFrame) = inletFrames[index];
			inlet.LocalFrame = worldFrame.RelativeTo(outletFrame);
			inlet.BranchOuterRadiusMillimetres = stagedPaths[index].OuterRadiusMillimetres;
			inlet.BranchPath = stagedPaths[index];
		}
	}

	public void RecalculateBranchPaths()
	{
		CadCollectorBranchPath[] solvedPaths = Inlets.Select(inlet =>
		{
			CadFrame worldFrame = GetWorldInletFrame(inlet);
			return CadCollectorBranchSolver.Solve(
				worldFrame,
				OutletFrame,
				inlet.BranchOuterRadiusMillimetres,
				inlet.BranchStartHandleLength,
				BranchEndHandleLength,
				inlet.BranchPath
			);
		}).ToArray();
		for (int index = 0; index < Inlets.Count; ++index)
		{
			Inlets[index].BranchPath = solvedPaths[index];
		}
	}

	public long CommitEdit()
	{
		long revision = Interlocked.Increment(ref generationRevision);
		ExactBuild.MarkStale(revision);
		return revision;
	}

	internal void SetGenerationRevision(long value)
	{
		Interlocked.Exchange(ref generationRevision, value);
	}

	internal CadCollectorSystem DeepClone()
	{
		CadCollectorSystem clone = new()
		{
			Id = Id,
			Name = Name,
			OutletFrame = OutletFrame,
			OutletProfile = OutletProfile,
			OutletStubLength = OutletStubLength,
			MergeLength = MergeLength,
			OverlapLength = OverlapLength,
			OutletTransitionSetback = OutletTransitionSetback,
			BranchEndHandleLength = BranchEndHandleLength,
			Inlets = Inlets.Select(inlet => inlet.DeepClone()).ToList(),
			IsResolved = IsResolved,
			Diagnostic = Diagnostic,
		};
		clone.SetGenerationRevision(GenerationRevision);
		clone.ExactBuild.Restore(ExactBuild.Snapshot);
		return clone;
	}
}

public readonly record struct RunnerEndpointConstraint(
	Guid CollectorSystemId,
	long GenerationRevision,
	Guid InletId,
	Guid TerminalBezierNodeId,
	CadFrame BezierEndFrame,
	CadFrame TerminalFrame,
	double EndHandleLength,
	Guid? ClockingTransitionNodeId,
	double ClockingTransitionLength
)
{
	public CadGenerationStamp Stamp =>
		new(CadGenerationOwnerKind.CollectorSystem, CollectorSystemId, GenerationRevision);
}

internal static class CollectorFrameDefaults
{
	internal static CadFrame Outlet =>
		new(new CadPoint3(400, 0, 0), new CadPoint3(1, 0, 0), new CadPoint3(0, 1, 0));

	internal static CadFrame Inlet =>
		new(CadPoint3.Zero, new CadPoint3(1, 0, 0), new CadPoint3(0, 1, 0));
}
