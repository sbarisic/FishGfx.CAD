// Versioned C ABI entry points for XCAF persistence and AP242 export.

}
namespace
{

static std::string gas_json_escape(const std::string& value)
{
	std::ostringstream stream;
	for (unsigned char character : value)
	{
		switch (character)
		{
		case '\"': stream << "\\\""; break;
		case '\\': stream << "\\\\"; break;
		case '\b': stream << "\\b"; break;
		case '\f': stream << "\\f"; break;
		case '\n': stream << "\\n"; break;
		case '\r': stream << "\\r"; break;
		case '\t': stream << "\\t"; break;
		default:
			if (character < 0x20)
			{
				stream << "\\u" << std::hex << std::setw(4)
					<< std::setfill('0') << static_cast<int>(character)
					<< std::dec;
			}
			else
			{
				stream << static_cast<char>(character);
			}
			break;
		}
	}
	return stream.str();
}

static std::string gas_patch_token(const std::string& value)
{
	std::string result;
	result.reserve(value.size());
	for (unsigned char character : value)
	{
		result.push_back(std::isalnum(character) ? static_cast<char>(character) : '_');
	}
	return result;
}

struct gas_edge_occurrence
{
	TopoDS_Edge edge;
	size_t count{};
};

static std::string gas_opening_json(
	const std::string& id,
	const std::string& patch_name,
	const std::string& role,
	const std::string& component_id,
	const std::vector<TopoDS_Face>& faces,
	const fgcad_point3&)
{
	if (faces.empty())
	{
		throw std::invalid_argument("Gas opening '" + id + "' has no published faces.");
	}

	double area = 0.0;
	gp_XYZ weighted_center(0.0, 0.0, 0.0);
	bool planar = true;
	std::vector<gas_edge_occurrence> occurrences;
	for (const TopoDS_Face& face : faces)
	{
		GProp_GProps properties;
		BRepGProp::SurfaceProperties(face, properties);
		double face_area = std::abs(properties.Mass());
		area += face_area;
		weighted_center += properties.CentreOfMass().XYZ() * face_area;
		planar = planar && BRepAdaptor_Surface(face).GetType() == GeomAbs_Plane;
		for (TopExp_Explorer explorer(face, TopAbs_EDGE); explorer.More(); explorer.Next())
		{
			TopoDS_Edge edge = TopoDS::Edge(explorer.Current());
			auto existing = std::find_if(
				occurrences.begin(), occurrences.end(),
				[&](const gas_edge_occurrence& item) { return item.edge.IsSame(edge); });
			if (existing == occurrences.end())
			{
				occurrences.push_back({ edge, 1 });
			}
			else
			{
				++existing->count;
			}
		}
	}
	if (!(area > Precision::Confusion()))
	{
		throw std::invalid_argument("Gas opening '" + id + "' has zero area.");
	}

	Handle(NCollection_HSequence<TopoDS_Shape>) boundary_edges =
		new NCollection_HSequence<TopoDS_Shape>();
	std::vector<double> edge_lengths;
	double perimeter = 0.0;
	for (const gas_edge_occurrence& occurrence : occurrences)
	{
		if (occurrence.count != 1) continue;
		boundary_edges->Append(occurrence.edge);
		GProp_GProps properties;
		BRepGProp::LinearProperties(occurrence.edge, properties);
		double length = std::abs(properties.Mass());
		perimeter += length;
		edge_lengths.push_back(length);
	}
	std::sort(edge_lengths.begin(), edge_lengths.end());
	Handle(NCollection_HSequence<TopoDS_Shape>) wires =
		ShapeAnalysis_FreeBounds::ConnectEdgesToWires(
			boundary_edges, std::max(Precision::Confusion() * 10.0, 1.0e-7), false);
	int loop_count = wires.IsNull() ? 0 : wires->Length();
	gp_XYZ center = weighted_center / area;
	BRepAdaptor_Surface opening_surface(faces.front());
	gp_Dir opening_normal = opening_surface.Plane().Axis().Direction();
	if (faces.front().Orientation() == TopAbs_REVERSED) opening_normal.Reverse();

	std::ostringstream stream;
	stream << std::setprecision(17)
		<< "{\"componentId\":\"" << gas_json_escape(component_id)
		<< "\",\"fingerprint\":{\"area\":" << area
		<< ",\"centroid\":[" << center.X() << ',' << center.Y() << ',' << center.Z()
		<< "],\"edgeLengths\":[";
	for (size_t index = 0; index < edge_lengths.size(); ++index)
	{
		if (index != 0) stream << ',';
		stream << edge_lengths[index];
	}
	stream << "],\"loopCount\":" << loop_count
		<< ",\"loopSamples\":[],\"normal\":[" << opening_normal.X() << ','
		<< opening_normal.Y() << ',' << opening_normal.Z() << "]"
		<< ",\"perimeter\":" << perimeter
		<< ",\"surfaceType\":\"" << (planar ? "plane" : "mixed") << "\"}"
		<< ",\"id\":\"" << gas_json_escape(id)
		<< "\",\"patchName\":\"" << gas_json_escape(patch_name)
		<< "\",\"role\":\"" << role << "\"}";
	return stream.str();
}

static std::string build_gas_manifest_json(const fgcad_document& document)
{
	std::vector<std::string> collector_ids;
	std::vector<std::string> member_runner_ids;
	for (const auto& item : document.collectors)
	{
		collector_ids.push_back(item.first);
		member_runner_ids.insert(
			member_runner_ids.end(), item.second.runner_ids.begin(), item.second.runner_ids.end());
	}
	std::sort(collector_ids.begin(), collector_ids.end());
	std::sort(member_runner_ids.begin(), member_runner_ids.end());
	member_runner_ids.erase(
		std::unique(member_runner_ids.begin(), member_runner_ids.end()), member_runner_ids.end());
	std::vector<std::string> runner_ids;
	for (const auto& item : document.runners)
	{
		if (!std::binary_search(member_runner_ids.begin(), member_runner_ids.end(), item.first))
		{
			runner_ids.push_back(item.first);
		}
	}
	std::sort(runner_ids.begin(), runner_ids.end());

	std::ostringstream stream;
	stream << std::setprecision(17)
		<< "{\"matchingPolicy\":{\"areaAbsoluteTolerance\":0.0001"
		<< ",\"areaRelativeTolerance\":0.00001,\"centroidToleranceMm\":0.01"
		<< ",\"loopSampleToleranceMm\":0.02,\"normalAngularToleranceDegrees\":0.1"
		<< ",\"perimeterRelativeTolerance\":0.00001,\"uniqueScoreMargin\":0.1"
		<< ",\"version\":1},\"paths\":[";
	bool first_path = true;
	for (const std::string& collector_id : collector_ids)
	{
		const collector_record& collector = document.collectors.at(collector_id);
		if (collector.gas_shape.IsNull())
		{
			throw std::invalid_argument("Collector '" + collector.name + "' has no published gas domain.");
		}
		if (collector.gas_entrance_face_groups.size() != collector.runner_ids.size()
			|| collector.gas_entrance_group_runner_ids.size() != collector.runner_ids.size()
			|| collector.gas_outlet_faces.empty())
		{
			throw std::invalid_argument(
				"Collector '" + collector.name + "' has no compatible published opening provenance; rebuild exact geometry.");
		}
		if (!first_path) stream << ',';
		first_path = false;
		stream << "{\"componentName\":\"FGGASPATH:V1:COLLECTOR:"
			<< gas_json_escape(collector.id) << ':' << gas_json_escape(encode_label_text(collector.name))
			<< "\",\"id\":\"" << gas_json_escape(collector.id)
			<< "\",\"kind\":\"collector\",\"name\":\"" << gas_json_escape(collector.name)
			<< "\",\"openings\":[";
		for (size_t index = 0; index < collector.runner_ids.size(); ++index)
		{
			if (index != 0) stream << ',';
			const std::string& runner_id = collector.runner_ids[index];
			const runner_record& runner = document.runners.at(runner_id);
			auto group = std::find(
				collector.gas_entrance_group_runner_ids.begin(),
				collector.gas_entrance_group_runner_ids.end(),
				runner_id);
			if (group == collector.gas_entrance_group_runner_ids.end())
				throw std::invalid_argument("Collector opening provenance is missing a member runner.");
			size_t group_index = static_cast<size_t>(std::distance(
				collector.gas_entrance_group_runner_ids.begin(), group));
			fgcad_point3 outward{
				-runner.gas_start_frame.tangent.x,
				-runner.gas_start_frame.tangent.y,
				-runner.gas_start_frame.tangent.z };
			stream << gas_opening_json(
				"collector:" + collector.id + ":runner:" + runner_id + ":start",
				"inlet_" + gas_patch_token(runner_id), "inlet", runner_id,
				collector.gas_entrance_face_groups[group_index], outward);
		}
		if (!collector.runner_ids.empty()) stream << ',';
		stream << gas_opening_json(
			"collector:" + collector.id + ":outlet",
			"outlet_" + gas_patch_token(collector.id), "outlet", collector.id,
			collector.gas_outlet_faces, collector.geometry_spec.outlet_frame.tangent);
		stream << "]}";
	}
	for (const std::string& runner_id : runner_ids)
	{
		const runner_record& runner = document.runners.at(runner_id);
		if (runner.gas_shape.IsNull() || runner.gas_start_cap.IsNull() || runner.gas_end_cap.IsNull())
		{
			throw std::invalid_argument(
				"Runner '" + runner.name + "' has no compatible published opening provenance; rebuild exact geometry.");
		}
		if (!first_path) stream << ',';
		first_path = false;
		stream << "{\"componentName\":\"FGGASPATH:V1:RUNNER:"
			<< gas_json_escape(runner.id) << ':' << gas_json_escape(encode_label_text(runner.name))
			<< "\",\"id\":\"" << gas_json_escape(runner.id)
			<< "\",\"kind\":\"runner\",\"name\":\"" << gas_json_escape(runner.name)
			<< "\",\"openings\":[";
		fgcad_point3 start_normal{
			-runner.gas_start_frame.tangent.x,
			-runner.gas_start_frame.tangent.y,
			-runner.gas_start_frame.tangent.z };
		stream << gas_opening_json(
			"runner:" + runner.id + ":start", "inlet_" + gas_patch_token(runner.id),
			"inlet", runner.id, { runner.gas_start_cap }, start_normal) << ',';
		stream << gas_opening_json(
			"runner:" + runner.id + ":end", "outlet_" + gas_patch_token(runner.id),
			"outlet", runner.id, { runner.gas_end_cap }, runner.gas_end_frame.tangent);
		stream << "]}";
	}
	stream << "],\"schema\":\"fishgfx.gas-patches\",\"units\":\"mm\",\"version\":1}";
	return stream.str();
}

}
extern "C"
{

fgcad_status fgcad_document_save_xcaf(fgcad_document* document, const char* path_utf8)
{
	return guarded([&]()
	{
		if (document == nullptr)
		{
			throw std::invalid_argument("The document cannot be null.");
		}
		if (!document->staged_runner_id.empty()
			|| !document->staged_collector_id.empty())
		{
			throw std::invalid_argument(
				"A project snapshot cannot be saved while exact geometry publication is staged.");
		}

		std::string path = require_text(path_utf8, "path_utf8");
		Handle(TDocStd_Document) xcaf = make_xcaf_document(
			document->parts,
			document->runners,
			document->selectors,
			document->collectors,
			true
		);
		Handle(XCAFApp_Application) application = XCAFApp_Application::GetApplication();
		PCDM_StoreStatus status = application->SaveAs(xcaf, extended(path));
		application->Close(xcaf);

		if (status != PCDM_SS_OK)
		{
			last_error = "The XCAF binary document could not be saved.";
			return FGCAD_STATUS_IO_FAILED;
		}

		return FGCAD_STATUS_OK;
	});
}

fgcad_status fgcad_document_load_xcaf(fgcad_document* document, const char* path_utf8)
{
	return guarded([&]()
	{
		if (document == nullptr)
		{
			throw std::invalid_argument("The document cannot be null.");
		}

		Handle(TDocStd_Document) xcaf;
		Handle(XCAFApp_Application) application = XCAFApp_Application::GetApplication();
		BinXCAFDrivers::DefineFormat(application);
		PCDM_ReaderStatus status = application->Open(
			extended(require_text(path_utf8, "path_utf8")),
			xcaf
		);

		if (status != PCDM_RS_OK)
		{
			last_error = "The XCAF binary document could not be opened (status "
				+ std::to_string(static_cast<int>(status)) + ").";
			return FGCAD_STATUS_IO_FAILED;
		}

		Handle(XCAFDoc_ShapeTool) shapes = XCAFDoc_DocumentTool::ShapeTool(xcaf->Main());
		NCollection_Sequence<TDF_Label> roots;
		shapes->GetFreeShapes(roots);
		fgcad_document replacement;

		auto load_component = [&](const TDF_Label& label)
		{
			std::string name = label_name(label);
			TDF_Label referred;
			bool is_reference = XCAFDoc_ShapeTool::GetReferredShape(label, referred);
			TopoDS_Shape shape = shapes->GetShape(is_reference ? referred : label);
			gp_Trsf placement = XCAFDoc_ShapeTool::GetLocation(label).Transformation();

			if (name.rfind("FGGASOPENING:V1:", 0) == 0)
			{
				std::string fields = name.substr(16);
				size_t first = fields.find(':');
				size_t second = fields.find(':', first == std::string::npos ? first : first + 1);
				if (first == std::string::npos || second == std::string::npos) return;
				std::string kind = fields.substr(0, first);
				std::string id = fields.substr(first + 1, second - first - 1);
				std::string role = fields.substr(second + 1);
				std::vector<TopoDS_Face> faces;
				TopoDS_Shape placed_shape = shape.Moved(TopLoc_Location(placement));
				for (TopExp_Explorer explorer(placed_shape, TopAbs_FACE); explorer.More(); explorer.Next())
					faces.push_back(TopoDS::Face(explorer.Current()));
				if (faces.empty()) return;
				if (kind == "RUNNER")
				{
					runner_record& runner = replacement.runners[id];
					runner.id = id;
					if (role == "START") runner.gas_start_cap = faces.front();
					else if (role == "END") runner.gas_end_cap = faces.front();
				}
				else if (kind == "COLLECTOR")
				{
					collector_record& collector = replacement.collectors[id];
					collector.id = id;
					if (role == "OUTLET") collector.gas_outlet_faces = std::move(faces);
					else if (role.rfind("INLET:", 0) == 0)
					{
						collector.gas_entrance_group_runner_ids.push_back(role.substr(6));
						collector.gas_entrance_face_groups.push_back(faces);
						collector.gas_entrance_faces.insert(
							collector.gas_entrance_faces.end(), faces.begin(), faces.end());
					}
				}
				return;
			}

			if (name.rfind("FGRUNNERGAS:", 0) == 0)
			{
				std::string id = name.substr(12);
				runner_record& runner = replacement.runners[id];
				runner.id = id;
				runner.gas_shape = shape.Moved(TopLoc_Location(placement));
				return;
			}

			if (name.rfind("FGCOLLECTORGAS:", 0) == 0)
			{
				std::string id = name.substr(15);
				collector_record& collector = replacement.collectors[id];
				collector.id = id;
				collector.gas_shape = shape.Moved(TopLoc_Location(placement));
				return;
			}

			if (name == "FGRUNNER" || name.rfind("FGRUNNER:", 0) == 0
				|| name.rfind("FGRUNNERDEF:", 0) == 0)
			{
				runner_record runner;
				if (name == "FGRUNNER")
				{
					runner.id = "legacy-runner";
					runner.name = "Runner 1";
				}
				else if (name.rfind("FGRUNNERDEF:", 0) == 0)
				{
					size_t separator = name.find(':', 12);
					runner.id = separator == std::string::npos
						? name.substr(12)
						: name.substr(12, separator - 12);
					runner.name = separator == std::string::npos
						? "Runner"
						: name.substr(separator + 1);
				}
				else
				{
					size_t separator = name.find(':', 9);
					runner.id = separator == std::string::npos ? name.substr(9) : name.substr(9, separator - 9);
					runner.name = separator == std::string::npos ? "Runner" : name.substr(separator + 1);
				}
				auto existing = replacement.runners.find(runner.id);
				if (existing != replacement.runners.end())
				{
					runner.gas_shape = existing->second.gas_shape;
					runner.gas_start_cap = existing->second.gas_start_cap;
					runner.gas_end_cap = existing->second.gas_end_cap;
				}
				runner.shape = shape.Moved(TopLoc_Location(placement));
				replacement.runners[runner.id] = std::move(runner);
				return;
			}

			if (name.rfind("FGCOLLECTOR:", 0) == 0)
			{
				std::string fields = name.substr(12);
				bool version_two = fields.rfind("V2:", 0) == 0;
				if (version_two)
				{
					fields = fields.substr(3);
				}
				size_t first = fields.find(':');
				size_t second = fields.find(':', first == std::string::npos ? first : first + 1);
				collector_record collector;
				collector.id = first == std::string::npos ? fields : fields.substr(0, first);
				collector.name = first == std::string::npos
					? "Collector"
					: version_two
						? decode_label_text(fields.substr(
							first + 1,
							second == std::string::npos
								? std::string::npos
								: second - first - 1))
						: fields.substr(first + 1, second == std::string::npos
							? std::string::npos
							: second - first - 1);
				if (second != std::string::npos)
				{
					std::string members = fields.substr(second + 1);
					size_t begin = 0;
					while (begin < members.size())
					{
						size_t comma = members.find(',', begin);
						collector.runner_ids.push_back(members.substr(
							begin,
							comma == std::string::npos ? std::string::npos : comma - begin));
						if (comma == std::string::npos) break;
						begin = comma + 1;
					}
				}
				auto existing = replacement.collectors.find(collector.id);
				if (existing != replacement.collectors.end())
				{
					collector.gas_shape = existing->second.gas_shape;
					collector.gas_outlet_faces = existing->second.gas_outlet_faces;
					collector.gas_entrance_faces = existing->second.gas_entrance_faces;
					collector.gas_entrance_face_groups = existing->second.gas_entrance_face_groups;
					collector.gas_entrance_group_runner_ids =
						existing->second.gas_entrance_group_runner_ids;
				}
				collector.shape = shape.Moved(TopLoc_Location(placement));
				replacement.collectors[collector.id] = std::move(collector);
				return;
			}

			if (name.rfind("FGPART:", 0) != 0)
			{
				return;
			}

			size_t separator = name.find(':', 7);
			part_record part;
			part.id = separator == std::string::npos ? name.substr(7) : name.substr(7, separator - 7);
			part.name = separator == std::string::npos ? "Part" : name.substr(separator + 1);
			part.shape = shape;
			part.placement = placement;
			part.source_document = xcaf;
			part.source_root = is_reference ? referred : label;
			rebuild_topology(part);
			replacement.parts[part.id] = std::move(part);

			for (TDF_ChildIterator child(label, false); child.More(); child.Next())
			{
				std::string selector_name = label_name(child.Value());

				if (selector_name.rfind("FGSELECTOR:", 0) != 0)
				{
					continue;
				}

				std::string fields = selector_name.substr(11);
				size_t first = fields.find(':');
				size_t second = fields.find(':', first == std::string::npos ? first : first + 1);

				if (first == std::string::npos || second == std::string::npos)
				{
					continue;
				}

				selector_record selector;
				selector.id = fields.substr(0, first);
				selector.part_id = fields.substr(first + 1, second - first - 1);
				selector.topology_id = std::stoull(fields.substr(second + 1));
				replacement.selectors[selector.id] = std::move(selector);
			}
		};

		for (int index = 1; index <= roots.Length(); ++index)
		{
			TDF_Label root = roots.Value(index);

			if (label_name(root) == "FGASSEMBLY")
			{
				NCollection_Sequence<TDF_Label> components;
				XCAFDoc_ShapeTool::GetComponents(root, components, false);

				for (int component_index = 1; component_index <= components.Length(); ++component_index)
				{
					load_component(components.Value(component_index));
				}
			}
			else
			{
				load_component(root);
			}
		}

		document->parts = std::move(replacement.parts);
		document->runners = std::move(replacement.runners);
		document->selectors = std::move(replacement.selectors);
		document->collectors = std::move(replacement.collectors);
		document->staged_runners.clear();
		document->runner_build_cache.clear();
		document->staged_runner_id.clear();
		document->staged_previous_runner = runner_record{};
		document->staged_previous_runner_exists = false;
		document->staged_runner_published = false;
		document->staged_collector_id.clear();
		document->staged_generation_revision = 0;
		document->staged_previous_collector = collector_record{};
		document->staged_previous_collector_exists = false;
		document->staged_collector_published = false;
		document->staged_previous_member_runners.clear();
		document->staged_missing_member_runners.clear();
		document->build_metrics.clear();
		document->tessellation_cache.clear();
		document->tessellation_cache_order.clear();
		++document->source_geometry_revision;

		return FGCAD_STATUS_OK;
	});
}

fgcad_status fgcad_document_export_step_ap242(fgcad_document* document, const char* path_utf8)
{
	return guarded([&]()
	{
		if (document == nullptr || document->runners.empty() && document->collectors.empty()
			|| std::any_of(document->runners.begin(), document->runners.end(), [](const auto& item)
			{
				return item.second.shape.IsNull();
			})
			|| std::any_of(document->collectors.begin(), document->collectors.end(), [](const auto& item)
			{
				return item.second.shape.IsNull();
			}))
		{
			throw std::invalid_argument("A valid exact runner is required before STEP export.");
		}
		if (!document->staged_runner_id.empty()
			|| !document->staged_collector_id.empty())
		{
			throw std::invalid_argument(
				"STEP export cannot read an uncommitted exact-geometry publication.");
		}

		Handle(TDocStd_Document) xcaf = make_xcaf_document(
			document->parts,
			document->runners,
			document->selectors,
			document->collectors,
			false
		);
		STEPCAFControl_Writer writer;
		Interface_Static::SetCVal("write.step.schema", "AP242DIS");

		if (!writer.Perform(xcaf, require_text(path_utf8, "path_utf8").c_str()))
		{
			last_error = "STEPCAFControl_Writer failed to export the AP242 assembly.";
			return FGCAD_STATUS_IO_FAILED;
		}

		return FGCAD_STATUS_OK;
	});
}

fgcad_status fgcad_document_export_gas_step_ap242(
	fgcad_document* document,
	const char* path_utf8)
{
	return guarded([&]()
	{
		if (document == nullptr
			|| document->runners.empty() && document->collectors.empty())
		{
			throw std::invalid_argument(
				"At least one published gas path is required before gas STEP export.");
		}
		if (!document->staged_runner_id.empty()
			|| !document->staged_collector_id.empty())
		{
			throw std::invalid_argument(
				"Gas STEP export cannot read an uncommitted exact-geometry publication.");
		}

		Handle(TDocStd_Document) xcaf = make_gas_xcaf_document(
			document->runners,
			document->collectors);
		STEPCAFControl_Writer writer;
		Interface_Static::SetCVal("write.step.schema", "AP242DIS");
		if (!writer.Perform(xcaf, require_text(path_utf8, "path_utf8").c_str()))
		{
			last_error = "STEPCAFControl_Writer failed to export the gas-only AP242 assembly.";
			return FGCAD_STATUS_IO_FAILED;
		}

		return FGCAD_STATUS_OK;
	});
}

fgcad_status fgcad_document_get_gas_manifest_json_size(
	fgcad_document* document,
	size_t* byte_count)
{
	return guarded([&]()
	{
		if (document == nullptr || byte_count == nullptr)
		{
			throw std::invalid_argument("Document and byte count are required.");
		}
		std::string json = build_gas_manifest_json(*document);
		*byte_count = json.size() + 1;
		return FGCAD_STATUS_OK;
	});
}

fgcad_status fgcad_document_copy_gas_manifest_json(
	fgcad_document* document,
	char* utf8_json,
	size_t byte_capacity)
{
	return guarded([&]()
	{
		if (document == nullptr || utf8_json == nullptr)
		{
			throw std::invalid_argument("Document and JSON destination are required.");
		}
		std::string json = build_gas_manifest_json(*document);
		if (byte_capacity < json.size() + 1)
		{
			throw std::invalid_argument("The gas manifest JSON buffer is too small.");
		}
		std::memcpy(utf8_json, json.c_str(), json.size() + 1);
		return FGCAD_STATUS_OK;
	});
}
