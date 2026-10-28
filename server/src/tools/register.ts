import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";

// Explicit list instead of scanning the directory: build artefacts (.d.ts, .map, stale files)
// can no longer register tools by accident, and a missing module is reported by name.
const TOOL_MODULES = [
  "ai_element_filter",
  "analyze_model_statistics",
  "color_elements",
  "create_dimensions",
  "create_grid",
  "create_level",
  "create_line_based_element",
  "create_point_based_element",
  "create_room",
  "create_structural_framing_system",
  "create_surface_based_element",
  "delete_element",
  "export_room_data",
  "get_available_family_types",
  "get_current_view_elements",
  "get_current_view_info",
  "get_element_parameters",
  "get_material_quantities",
  "get_project_units",
  "get_selected_elements",
  "operate_element",
  "query_stored_data",
  "say_hello",
  "send_code_to_revit",
  "set_element_parameters",
  "store_project_data",
  "store_room_data",
  "tag_all_rooms",
  "tag_all_walls",
  "write_lease",
];

export async function registerTools(server: McpServer) {
  for (const name of TOOL_MODULES) {
    try {
      // Imported one by one so a failing optional dependency (e.g. the native SQLite module
      // used by the store_* tools) disables only its own tools, not the whole server.
      const module = await import(`./${name}.js`);
      const registerFunctionName = Object.keys(module).find(
        (key) => key.startsWith("register") && typeof module[key] === "function"
      );

      if (registerFunctionName) {
        module[registerFunctionName](server);
      } else {
        console.error(`Tool module ${name} has no register function`);
      }
    } catch (error) {
      console.error(`Failed to register tool ${name}:`, error);
    }
  }
}
