// Focused native geometry services for FishGfx.CFD.

}
namespace
{
template<size_t size>
void cfd_copy_text(char (&destination)[size], const std::string& value)
{
	std::memset(destination, 0, size);
	std::memcpy(destination, value.data(), std::min(value.size(), size - 1));
}

bool cfd_faces_share_edge(const TopoDS_Face& left, const TopoDS_Face& right)
{
	for (TopExp_Explorer a(left, TopAbs_EDGE); a.More(); a.Next())
	{
		for (TopExp_Explorer b(right, TopAbs_EDGE); b.More(); b.Next())
		{
			if (a.Current().IsSame(b.Current())) return true;
		}
	}
	return false;
}

bool cfd_faces_coplanar(const TopoDS_Face& left, const TopoDS_Face& right, double tolerance)
{
	BRepAdaptor_Surface a(left);
	BRepAdaptor_Surface b(right);
	if (a.GetType() != GeomAbs_Plane || b.GetType() != GeomAbs_Plane) return false;
	gp_Pln pa = a.Plane();
	gp_Pln pb = b.Plane();
	return std::abs(pa.Axis().Direction().Dot(pb.Axis().Direction())) >= 1.0 - 1.0e-10
		&& pa.Distance(pb.Location()) <= tolerance;
}

std::vector<std::vector<TopoDS_Face>> cfd_planar_regions(const TopoDS_Shape& shape)
{
	std::vector<TopoDS_Face> faces;
	for (TopExp_Explorer explorer(shape, TopAbs_FACE); explorer.More(); explorer.Next())
	{
		TopoDS_Face face = TopoDS::Face(explorer.Current());
		if (BRepAdaptor_Surface(face).GetType() == GeomAbs_Plane) faces.push_back(face);
	}
	std::vector<std::vector<TopoDS_Face>> result;
	std::vector<bool> visited(faces.size());
	for (size_t start = 0; start < faces.size(); ++start)
	{
		if (visited[start]) continue;
		std::vector<TopoDS_Face> region;
		std::vector<size_t> pending{ start };
		visited[start] = true;
		while (!pending.empty())
		{
			size_t current = pending.back();
			pending.pop_back();
			region.push_back(faces[current]);
			for (size_t candidate = 0; candidate < faces.size(); ++candidate)
			{
				if (!visited[candidate]
					&& cfd_faces_share_edge(faces[current], faces[candidate])
					&& cfd_faces_coplanar(faces[current], faces[candidate], 1.0e-5))
				{
					visited[candidate] = true;
					pending.push_back(candidate);
				}
			}
		}
		result.push_back(std::move(region));
	}
	return result;
}

struct cfd_region_fingerprint
{
	double area{};
	gp_Pnt centroid;
	gp_Dir normal{ 0, 0, 1 };
	double perimeter{};
	uint32_t loop_count{};
	std::vector<double> edge_lengths;
};

cfd_region_fingerprint cfd_fingerprint(const std::vector<TopoDS_Face>& faces)
{
	cfd_region_fingerprint result;
	gp_XYZ weighted(0, 0, 0);
	struct occurrence { TopoDS_Edge edge; size_t count{}; };
	std::vector<occurrence> edges;
	for (const TopoDS_Face& face : faces)
	{
		GProp_GProps properties;
		BRepGProp::SurfaceProperties(face, properties);
		double area = std::abs(properties.Mass());
		result.area += area;
		weighted += properties.CentreOfMass().XYZ() * area;
		for (TopExp_Explorer explorer(face, TopAbs_EDGE); explorer.More(); explorer.Next())
		{
			TopoDS_Edge edge = TopoDS::Edge(explorer.Current());
			auto found = std::find_if(edges.begin(), edges.end(), [&](const occurrence& item)
			{
				return item.edge.IsSame(edge);
			});
			if (found == edges.end()) edges.push_back({ edge, 1 }); else ++found->count;
		}
	}
	if (!(result.area > Precision::Confusion())) throw std::runtime_error("A candidate opening has zero area.");
	result.centroid = gp_Pnt(weighted / result.area);
	BRepAdaptor_Surface surface(faces.front());
	result.normal = surface.Plane().Axis().Direction();
	if (faces.front().Orientation() == TopAbs_REVERSED) result.normal.Reverse();
	Handle(NCollection_HSequence<TopoDS_Shape>) boundary = new NCollection_HSequence<TopoDS_Shape>();
	for (const occurrence& item : edges)
	{
		if (item.count != 1) continue;
		boundary->Append(item.edge);
		GProp_GProps properties;
		BRepGProp::LinearProperties(item.edge, properties);
		double length = std::abs(properties.Mass());
		result.perimeter += length;
		result.edge_lengths.push_back(length);
	}
	std::sort(result.edge_lengths.begin(), result.edge_lengths.end());
	Handle(NCollection_HSequence<TopoDS_Shape>) wires =
		ShapeAnalysis_FreeBounds::ConnectEdgesToWires(boundary, 1.0e-6, false);
	result.loop_count = wires.IsNull() ? 0 : static_cast<uint32_t>(wires->Length());
	return result;
}

double cfd_distance(const gp_Pnt& left, const fgcad_point3& right)
{
	return left.Distance(gp_Pnt(right.x, right.y, right.z));
}

bool cfd_face_in_regions(
	const TopoDS_Face& face,
	const std::vector<cfd_opening_region>& regions)
{
	for (const cfd_opening_region& region : regions)
	{
		for (const TopoDS_Face& candidate : region.faces)
		{
			if (face.IsSame(candidate)) return true;
		}
	}
	return false;
}

void cfd_write_stl_faces(
	std::ostream& stream,
	const std::string& name,
	const TopoDS_Shape& shape,
	const std::vector<TopoDS_Face>* selected,
	const std::vector<cfd_opening_region>& openings)
{
	stream << "solid " << name << '\n' << std::setprecision(17);
	for (TopExp_Explorer explorer(shape, TopAbs_FACE); explorer.More(); explorer.Next())
	{
		TopoDS_Face face = TopoDS::Face(explorer.Current());
		bool include = selected == nullptr ? !cfd_face_in_regions(face, openings)
			: std::any_of(selected->begin(), selected->end(), [&](const TopoDS_Face& item)
			{
				return face.IsSame(item);
			});
		if (!include) continue;
		TopLoc_Location location;
		Handle(Poly_Triangulation) triangulation = BRep_Tool::Triangulation(face, location);
		if (triangulation.IsNull()) throw std::runtime_error("CFD STL tessellation produced a face without triangles.");
		for (int index = 1; index <= triangulation->NbTriangles(); ++index)
		{
			int a;
			int b;
			int c;
			triangulation->Triangle(index).Get(a, b, c);
			if (face.Orientation() == TopAbs_REVERSED) std::swap(b, c);
			gp_Pnt pa = triangulation->Node(a).Transformed(location.Transformation());
			gp_Pnt pb = triangulation->Node(b).Transformed(location.Transformation());
			gp_Pnt pc = triangulation->Node(c).Transformed(location.Transformation());
			gp_Vec normal(pa, pb);
			normal.Cross(gp_Vec(pa, pc));
			if (normal.SquareMagnitude() <= Precision::SquareConfusion()) continue;
			normal.Normalize();
			stream << "  facet normal " << normal.X() << ' ' << normal.Y() << ' ' << normal.Z() << "\n"
				<< "    outer loop\n"
				<< "      vertex " << pa.X() * 0.001 << ' ' << pa.Y() * 0.001 << ' ' << pa.Z() * 0.001 << "\n"
				<< "      vertex " << pb.X() * 0.001 << ' ' << pb.Y() * 0.001 << ' ' << pb.Z() * 0.001 << "\n"
				<< "      vertex " << pc.X() * 0.001 << ' ' << pc.Y() * 0.001 << ' ' << pc.Z() * 0.001 << "\n"
				<< "    endloop\n  endfacet\n";
		}
	}
	stream << "endsolid " << name << '\n';
}
}
extern "C"
{

fgcad_status fgcad_cfd_geometry_import_step(const char* path_utf8, fgcad_cfd_geometry** output)
{
	return guarded([&]()
	{
		if (output == nullptr) throw std::invalid_argument("CFD geometry output is required.");
		auto result = std::make_unique<fgcad_cfd_geometry>();
		Handle(XCAFApp_Application) application = XCAFApp_Application::GetApplication();
		BinXCAFDrivers::DefineFormat(application);
		application->NewDocument("BinXCAF", result->document);
		STEPCAFControl_Reader reader;
		if (reader.ReadFile(require_text(path_utf8, "path_utf8").c_str()) != IFSelect_RetDone
			|| !reader.Transfer(result->document))
		{
			last_error = "The CFD gas STEP could not be imported.";
			return FGCAD_STATUS_IMPORT_FAILED;
		}
		Handle(XCAFDoc_ShapeTool) shapes = XCAFDoc_DocumentTool::ShapeTool(result->document->Main());
		NCollection_Sequence<TDF_Label> roots;
		shapes->GetFreeShapes(roots);
		for (int root_index = 1; root_index <= roots.Length(); ++root_index)
		{
			NCollection_Sequence<TDF_Label> components;
			XCAFDoc_ShapeTool::GetComponents(roots.Value(root_index), components, false);
			if (components.IsEmpty()) components.Append(roots.Value(root_index));
			for (int index = 1; index <= components.Length(); ++index)
			{
				TDF_Label component = components.Value(index);
				std::string name = label_name(component);
				if (name.rfind("FGGASPATH:V1:", 0) != 0) continue;
				std::string fields = name.substr(13);
				size_t first = fields.find(':');
				size_t second = fields.find(':', first == std::string::npos ? first : first + 1);
				if (first == std::string::npos || second == std::string::npos) continue;
				std::string kind = fields.substr(0, first);
				std::string id = fields.substr(first + 1, second - first - 1);
				std::string display_name = decode_label_text(fields.substr(second + 1));
				TDF_Label referred;
				bool reference = XCAFDoc_ShapeTool::GetReferredShape(component, referred);
				TopoDS_Shape shape = shapes->GetShape(reference ? referred : component);
				shape = shape.Moved(TopLoc_Location(XCAFDoc_ShapeTool::GetLocation(component).Transformation()));
				cfd_path_record path;
				cfd_copy_text(path.info.id, id);
				cfd_copy_text(path.info.name, display_name);
				cfd_copy_text(path.info.component_name, name);
				path.info.kind = kind == "COLLECTOR" ? FGCAD_CFD_PATH_COLLECTOR : FGCAD_CFD_PATH_RUNNER;
				path.shape = shape;
				result->paths.push_back(std::move(path));
			}
		}
		std::sort(result->paths.begin(), result->paths.end(), [](const cfd_path_record& a, const cfd_path_record& b)
		{
			return std::strcmp(a.info.id, b.info.id) < 0;
		});
		if (result->paths.empty()) throw std::invalid_argument("The STEP contains no FGGASPATH:V1 components.");
		*output = result.release();
		return FGCAD_STATUS_OK;
	});
}

void fgcad_cfd_geometry_destroy(fgcad_cfd_geometry* geometry)
{
	delete geometry;
}

fgcad_status fgcad_cfd_geometry_get_path_count(fgcad_cfd_geometry* geometry, size_t* count)
{
	return guarded([&]()
	{
		if (geometry == nullptr || count == nullptr) throw std::invalid_argument("CFD geometry and count are required.");
		*count = geometry->paths.size();
		return FGCAD_STATUS_OK;
	});
}

fgcad_status fgcad_cfd_geometry_copy_paths(
	fgcad_cfd_geometry* geometry,
	fgcad_cfd_path_info* paths,
	size_t capacity)
{
	return guarded([&]()
	{
		if (geometry == nullptr || paths == nullptr || capacity < geometry->paths.size())
			throw std::invalid_argument("The CFD path destination is too small.");
		for (size_t index = 0; index < geometry->paths.size(); ++index) paths[index] = geometry->paths[index].info;
		return FGCAD_STATUS_OK;
	});
}

fgcad_status fgcad_cfd_geometry_prepare_path(
	fgcad_cfd_geometry* geometry,
	const char* path_id,
	const fgcad_cfd_opening_spec* openings,
	size_t opening_count,
	const fgcad_cfd_matching_policy* policy,
	fgcad_cfd_match_result* results,
	size_t result_capacity,
	fgcad_cfd_geometry_info* info)
{
	return guarded([&]()
	{
		if (geometry == nullptr || openings == nullptr || policy == nullptr || results == nullptr
			|| info == nullptr || opening_count == 0 || result_capacity < opening_count || policy->version != 1)
		{
			throw std::invalid_argument("Complete version-1 CFD path matching arguments are required.");
		}
		std::string requested = require_text(path_id, "path_id");
		auto path = std::find_if(geometry->paths.begin(), geometry->paths.end(), [&](const cfd_path_record& item)
		{
			return requested == item.info.id;
		});
		if (path == geometry->paths.end()) throw std::out_of_range("The requested CFD gas path was not found.");
		if (count_shape_type(path->shape, TopAbs_SOLID) != 1
			|| !BRepCheck_Analyzer(path->shape, true, true).IsValid())
			throw std::invalid_argument("The selected CFD gas path is not one valid solid.");
		TopExp_Explorer imported_solid(path->shape, TopAbs_SOLID);
		TopoDS_Solid oriented_solid = TopoDS::Solid(imported_solid.Current());
		if (!BRepLib::OrientClosedSolid(oriented_solid))
			throw std::invalid_argument("The selected CFD gas path could not be oriented as a closed solid.");
		path->shape = oriented_solid;
		std::vector<std::vector<TopoDS_Face>> candidates = cfd_planar_regions(path->shape);
		std::vector<cfd_region_fingerprint> fingerprints;
		for (const auto& candidate : candidates) fingerprints.push_back(cfd_fingerprint(candidate));
		geometry->openings.clear();
		std::vector<size_t> selected_indices;
		double smallest_diameter = std::numeric_limits<double>::infinity();
		const fgcad_cfd_opening_spec* interior_seed = nullptr;
		for (size_t opening_index = 0; opening_index < opening_count; ++opening_index)
		{
			const fgcad_cfd_opening_spec& expected = openings[opening_index];
			struct score_record { size_t index; double score; uint32_t mask; };
			std::vector<score_record> scores;
			for (size_t candidate_index = 0; candidate_index < fingerprints.size(); ++candidate_index)
			{
				const cfd_region_fingerprint& actual = fingerprints[candidate_index];
				double area_limit = std::max(policy->area_absolute_tolerance,
					expected.area * policy->area_relative_tolerance);
				double area_delta = std::abs(actual.area - expected.area);
				double centroid_delta = cfd_distance(actual.centroid, expected.centroid);
				// A STEP round-trip may reverse a planar surface parameterization while
				// preserving the same oriented solid face. Treat the fingerprint normal
				// as an unoriented plane axis; centroid and profile geometry disambiguate
				// distinct openings.
				double dot = std::clamp(std::abs(actual.normal.Dot(gp_Dir(
					expected.normal.x, expected.normal.y, expected.normal.z))), 0.0, 1.0);
				double angle = std::acos(dot) * 180.0 / pi;
				double perimeter_limit = std::max(Precision::Confusion(),
					expected.perimeter * policy->perimeter_relative_tolerance);
				double perimeter_delta = std::abs(actual.perimeter - expected.perimeter);
				uint32_t mask = 0;
				if (area_delta > area_limit) mask |= 1;
				if (centroid_delta > policy->centroid_tolerance_mm) mask |= 2;
				if (angle > policy->normal_angular_tolerance_degrees) mask |= 4;
				if (actual.loop_count != expected.loop_count) mask |= 8;
				if (perimeter_delta > perimeter_limit) mask |= 16;
				double score = (area_delta / area_limit
					+ centroid_delta / policy->centroid_tolerance_mm
					+ angle / policy->normal_angular_tolerance_degrees
					+ perimeter_delta / perimeter_limit
					+ (actual.loop_count == expected.loop_count ? 0.0 : 1.0)) / 5.0;
				scores.push_back({ candidate_index, score, mask });
			}
			std::sort(scores.begin(), scores.end(), [](const score_record& a, const score_record& b)
			{
				return a.score < b.score;
			});
			fgcad_cfd_match_result& output = results[opening_index];
			std::memset(&output, 0, sizeof(output));
			cfd_copy_text(output.opening_id, expected.id);
			output.best_score = scores.empty() ? std::numeric_limits<double>::infinity() : scores[0].score;
			output.second_best_score = scores.size() < 2 ? std::numeric_limits<double>::infinity() : scores[1].score;
			output.failed_tolerance_mask = scores.empty() ? 0xffffffffu : scores[0].mask;
			std::vector<score_record> passing;
			std::copy_if(scores.begin(), scores.end(), std::back_inserter(passing), [](const score_record& item)
			{
				return item.mask == 0;
			});
			bool reused = !passing.empty()
				&& std::find(selected_indices.begin(), selected_indices.end(), passing[0].index)
					!= selected_indices.end();
			bool ambiguous = passing.size() > 1
				&& passing[1].score - passing[0].score < policy->unique_score_margin;
			if (passing.empty() || ambiguous || reused)
			{
				std::ostringstream diagnostic;
				diagnostic << "Gas opening '" << expected.id << "' did not have one unique STEP-face match: "
					<< "candidateCount=" << scores.size()
					<< ", passingCount=" << passing.size()
					<< ", bestScore=" << output.best_score
					<< ", secondBestScore=" << output.second_best_score
					<< ", failedToleranceMask=" << output.failed_tolerance_mask
					<< ", ambiguous=" << ambiguous
					<< ", alreadySelected=" << reused << '.';
				throw std::invalid_argument(diagnostic.str());
			}
			cfd_copy_text(output.selected_candidate, "planar-region-" + std::to_string(passing[0].index));
			selected_indices.push_back(passing[0].index);
			geometry->openings.push_back({ expected.id, expected.patch_name, expected.role, candidates[passing[0].index] });
			if (expected.role == FGCAD_CFD_OPENING_INLET)
			{
				smallest_diameter = std::min(smallest_diameter, 2.0 * std::sqrt(expected.area / pi));
				if (interior_seed == nullptr) interior_seed = &expected;
			}
		}
		geometry->selected_shape = path->shape;
		Bnd_Box bounds;
		BRepBndLib::Add(path->shape, bounds);
		double xmin, ymin, zmin, xmax, ymax, zmax;
		bounds.Get(xmin, ymin, zmin, xmax, ymax, zmax);
		geometry->info.minimum_mm = { xmin, ymin, zmin };
		geometry->info.maximum_mm = { xmax, ymax, zmax };
		geometry->info.smallest_inlet_hydraulic_diameter_mm = smallest_diameter;
		if (interior_seed == nullptr)
			throw std::invalid_argument("The selected CFD path has no inlet from which to seed the mesh interior.");
		gp_Pnt seed_centroid(
			interior_seed->centroid.x,
			interior_seed->centroid.y,
			interior_seed->centroid.z);
		gp_Dir seed_axis(
			interior_seed->normal.x,
			interior_seed->normal.y,
			interior_seed->normal.z);
		TopExp_Explorer solid_explorer(path->shape, TopAbs_SOLID);
		TopoDS_Solid solid = TopoDS::Solid(solid_explorer.Current());
		BRepClass3d_SolidClassifier classifier;
		classifier.Load(solid);
		classifier.PerformInfinitePoint(1.0e-6);
		TopAbs_State infinite_state = classifier.State();
		gp_Pnt interior;
		bool found_interior = false;
		for (double depth : { 0.1, 0.25, 0.5, 1.0, 2.0 })
		{
			for (double sign : { -1.0, 1.0 })
			{
				gp_Pnt candidate = seed_centroid.Translated(gp_Vec(seed_axis) * depth * sign);
				classifier.Perform(candidate, 1.0e-6);
				TopAbs_State state = classifier.State();
				if (state != TopAbs_ON && state != TopAbs_UNKNOWN && state != infinite_state)
				{
					interior = candidate;
					found_interior = true;
					break;
				}
			}
			if (found_interior) break;
		}
		if (!found_interior)
			throw std::runtime_error("No orientation-independent interior point could be found behind the inlet cap.");
		geometry->info.interior_point_mm = point(interior);
		*info = geometry->info;
		return FGCAD_STATUS_OK;
	});
}

fgcad_status fgcad_cfd_geometry_tessellate(
	fgcad_cfd_geometry* geometry,
	double linear_deflection,
	double angular_deflection,
	fgcad_tessellation** output)
{
	return guarded([&]()
	{
		if (geometry == nullptr || output == nullptr || geometry->selected_shape.IsNull())
			throw std::invalid_argument("A prepared CFD gas path is required before tessellation.");
		auto result = tessellate(geometry->selected_shape, linear_deflection, angular_deflection);
		*output = result.release();
		return FGCAD_STATUS_OK;
	});
}

fgcad_status fgcad_cfd_geometry_export_multi_region_stl(
	fgcad_cfd_geometry* geometry,
	const char* path_utf8)
{
	return guarded([&]()
	{
		if (geometry == nullptr || geometry->selected_shape.IsNull() || geometry->openings.empty())
			throw std::invalid_argument("A prepared CFD gas path is required before STL export.");
		BRepMesh_IncrementalMesh mesher(geometry->selected_shape, 0.05, false, pi / 36.0, true);
		mesher.Perform();
		if (!mesher.IsDone()) throw std::runtime_error("CFD STL tessellation failed.");
		std::ofstream stream(require_text(path_utf8, "path_utf8"));
		if (!stream) { last_error = "The CFD multi-region STL could not be opened."; return FGCAD_STATUS_IO_FAILED; }
		cfd_write_stl_faces(stream, "walls", geometry->selected_shape, nullptr, geometry->openings);
		for (const cfd_opening_region& opening : geometry->openings)
			cfd_write_stl_faces(stream, opening.patch_name, geometry->selected_shape, &opening.faces, geometry->openings);
		if (!stream) { last_error = "The CFD multi-region STL could not be written."; return FGCAD_STATUS_IO_FAILED; }
		return FGCAD_STATUS_OK;
	});
}
