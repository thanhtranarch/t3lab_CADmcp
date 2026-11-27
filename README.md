# T3Lab CAD MCP — add-in AutoCAD cho Claude

Add-in AutoCAD (giống T3Lab Revit MCP) cho phép Claude Desktop / Claude Code làm việc với **bản vẽ đang mở trong AutoCAD**:

- **Đọc:** truy vấn đối tượng (kèm length / area), text, attribute, layer, block, layout, style, xref; chụp màn hình để Claude xem.
- **Đo / bóc khối lượng:** khoảng cách, góc, diện tích, giao điểm; tổng số lượng / chiều dài / diện tích theo layer, loại, block; xuất dữ liệu block ra CSV cho Excel.
- **Vẽ / sửa:** vẽ 23 loại đối tượng (cả table, multileader, attribute definition, 8 kiểu dim, text có field), sửa thuộc tính (cả dynamic block, đỉnh polyline, ô table), move / copy / rotate / scale / mirror / align / array, offset / join / boundary / extend / trim / break / fillet / chamfer, sửa thẳng trong định nghĩa block (`space: "block:TÊN"`), tìm và thay text, tạo block, đồng bộ attribute, explode.
- **Quản lý:** tạo / đổi tên / xoá layer, block, layout, viewport, text style, dim style, linetype, view, group, page setup, layer state, UCS, xref; purge; lấy block / layer / style từ file khác.
- **Tham chiếu / Scan-to-BIM:** attach ảnh, PDF, DWF, DGN làm underlay; attach point cloud ReCap (.rcp / .rcs); Civil 3D: đọc alignment, profile, surface, pipe network, COGO point, cao độ trên surface, lý trình / offset; tạo COGO point và thêm điểm vào TIN surface.
- **File:** mở, tạo, lưu (kể cả bản cũ 2018…2000), lưu bản sao, đổi tên, xoá file bản vẽ; duyệt thư mục.
- **Xuất:** PDF / DWFx / DWF theo layout (`export_pdf`), publish nhiều bản vẽ vào 1 PDF (`publish_pdf`), page setup, DXF, WBLOCK ra .dwg mới (`export_entities`).
- **Lệnh:** chạy lệnh AutoCAD dạng dòng lệnh khi chưa có tool riêng.

Tổng cộng **67 tool**: 65 tool chạy trong AutoCAD + 2 tool của bridge. Mỗi thay đổi là 1 bước Undo. Bảng **Agent Cursors** cho xem AI đang làm gì (xem bên dưới).

```
Claude ──stdio MCP──> bridge.py (%APPDATA%\T3LabCAD) ──TCP 127.0.0.1:48900-48908──> T3LabCAD add-in trong AutoCAD
```

| Phần | Vai trò |
|---|---|
| `src/T3LabCAD.Loader` | DLL AutoCAD load (bundle autoloader). Lệnh `T3MCP` (Start / Stop) `/ T3MCPSTART / T3MCPSTOP / T3MCPSTATUS / T3MCPRELOAD / T3CURSORS / T3MCPCONNECT`, panel **T3Lab MCP** trong tab ribbon **Add-ins** có sẵn của AutoCAD (không tạo tab riêng). Nạp Core từ bytes nên Core thay được khi AutoCAD đang chạy. |
| `src/T3LabCAD.Core` | Server TCP (chỉ localhost), chạy tool trên main thread của AutoCAD, mỗi thay đổi = 1 bước Undo. |
| `bridge/t3lab_cad_bridge.py` | MCP server stdio (chỉ dùng thư viện chuẩn Python). Kết nối được cả khi AutoCAD đóng, tự cache danh sách tool, hỗ trợ nhiều AutoCAD cùng lúc. |

Hỗ trợ **AutoCAD và Civil 3D 2022 → 2027** (và các sản phẩm dựa trên AutoCAD), một bundle cho mọi bản:

| Series | AutoCAD / Civil 3D | .NET | Thư mục trong bundle |
|---|---|---|---|
| R24 | 2022–2024 (2021 vẫn nạp) | .NET Framework 4.8 | `Contents/R24` |
| R25 | 2025–2026 | .NET 8 | `Contents/R25` |
| R26 | 2027 | .NET 10 | `Contents/R26` |

API AutoCAD lấy từ gói NuGet `AutoCAD.NET` của Autodesk, nên build được mọi series mà không cần cài AutoCAD.

## Cài đặt

**Cách dễ nhất:** trong Revit, T3Lab › MCP Control › Connect › chọn AutoCAD (hoặc Civil 3D) › **Connect**. MCP Control tải `T3LabCAD.zip` từ bản phát hành mới nhất trên GitHub, cài bundle + bridge, rồi kết nối AI app. Khởi động lại AutoCAD sau lần cài đầu.

**Từ mã nguồn** — cần .NET SDK 10 (build cả 3 series), Python 3:

```bash
powershell -ExecutionPolicy Bypass -File scripts/install.ps1
```

Script sẽ:
1. Build 2 project cho từng series R24, R25, R26 (`-Series R26` để chỉ build một series).
2. Copy bundle vào `%APPDATA%\Autodesk\ApplicationPlugins\T3LabCAD.bundle` (vị trí trusted, AutoCAD tự load khi khởi động).
3. Copy bridge vào `%APPDATA%\T3LabCAD\bridge.py`.
4. Thêm server `t3lab-autocad` vào `claude_desktop_config.json` (có backup) bằng `register_mcp.py` (copy vào `%APPDATA%\T3LabCAD`). Thêm `-ClaudeCode` để đăng ký cho Claude Code CLI. AI app khác: nút **Connect AI** trong AutoCAD.

Sau đó **khởi động lại Claude Desktop**. Trong AutoCAD gõ `T3MCPSTATUS` (hoặc nút **Status**) để kiểm tra — cùng các dòng với Status của T3Lab Revit MCP:

```
T3Lab CAD MCP v1.5.0: listening on 127.0.0.1:48900
65 tools · 0 calls · last: none
Queue: free
Agent Cursors: off
Log: %APPDATA%\T3LabCAD\logs\t3labcad.log
```

Nếu AutoCAD đang mở lúc cài lần đầu: khởi động lại AutoCAD, hoặc `NETLOAD` file `T3LabCAD.Loader.dll` trong thư mục bundle.

## Tool

| Nhóm | Tool |
|---|---|
| Trạng thái | `get_status`, `get_sysvars`, `set_sysvar`, `t3lab_narrate` (mục tiêu + các bước cho bảng Agent Cursors) |
| Bản vẽ | `open_document` (tên không có thư mục = cạnh bản vẽ đang mở), `new_document` (`save_as`), `activate_document`, `save_document` (dwg / xuất dxf, `copy=true` lưu bản sao, `version` 2018/2013/2010/2007/2004/2000), `close_document` |
| File | `list_drawing_files` (duyệt thư mục, đánh dấu file đang mở), `rename_document` (đổi tên file, bản vẽ đang mở được đóng → đổi tên → mở lại), `delete_drawing_file` (vào Recycle Bin, trừ khi `permanent=true`) |
| Layout | `list_layouts`, `activate_layout`, `create_layout` (mới hoặc copy từ layout khác), `add_viewport` (vùng model + tỉ lệ 1:100), `set_viewport` (tỉ lệ, tâm, khoá, on/off, freeze layer riêng trong viewport) |
| Page setup / layer state | `set_page_setup` (tạo / sửa page setup: máy in, khổ giấy, ctb, vùng in, tỉ lệ; gán cho layout, `*` = mọi layout), `layer_state` (list, save, restore, delete, rename, export / import .las) |
| Export / publish | `export_pdf` (plot layout của bản vẽ đang mở ra PDF / DWFx / DWF: 1 file nhiều trang hoặc mỗi layout 1 file; đổi khổ giấy, ctb, vùng in, tỉ lệ; `list_options` liệt kê máy in, khổ giấy, ctb), `publish_pdf` (gộp layout của nhiều bản vẽ, kể cả file đang đóng, vào 1 PDF theo page setup). DXF: `save_document path=*.dxf`; ảnh PNG: `screenshot`; WBLOCK: `export_entities` (entity hoặc block → .dwg mới, kèm layer / style, `version` bản cũ) |
| Layer | `list_layers`, `set_layer` (tạo/sửa/đổi tên/on/freeze/lock/current), `delete_layer` (có `merge_into`) |
| Style / linetype | `set_text_style` (font TrueType hoặc SHX, height, width factor, oblique), `set_dim_style` (tạo từ style gốc, đặt mọi biến DIM*, tự cập nhật dim), `load_linetype` (từ acadiso.lin, có wildcard) |
| Đối tượng có tên | `list_named_objects`, `rename_object`, `delete_object`, `purge` — cho layer, block, layout, text_style, dim_style, linetype, view, ucs, group, mleader_style, table_style, page_setup (xref chỉ list); `import_from_drawing` (lấy block / layer / style / linetype từ .dwg / .dwt khác, `replace=true` định nghĩa lại) |
| Group / view / xref | `set_group` (tạo, thêm/bớt entity), `save_view`, `restore_view`, `set_ucs` (get / world / set / save / restore; kèm geolocation), `attach_xref` (attach/overlay, đường dẫn tương đối), `manage_xref` (detach/reload/unload/bind/insert/repath) |
| Underlay / point cloud | `attach_underlay` (ảnh png / jpg / tif..., trang PDF, sheet DWF / DWFx, model DGN), `attach_point_cloud` (ReCap .rcp / .rcs cho Scan-to-BIM) |
| Civil 3D (chỉ khi chạy Civil 3D) | `civil_query` (summary, alignment, profile, surface, pipe_network, cogo_point; `elevation_at` cao độ surface tại điểm, `station_offset` lý trình / offset), `civil_points` (tạo COGO point, thêm điểm vào TIN surface, tạo surface nếu chưa có) |
| Đọc | `query_entities` (lọc theo type, layer, block, text, màu, linetype, closed, vùng chọn, handle; `totals` cộng length / area; `detail=full` có length, area, ô table; phân trang), `get_texts` (TEXT/MTEXT/ATTRIB/MLEADER), `list_blocks`, `selection` |
| Đo / bóc khối lượng | `measure` (distance, angle, area, length, bbox, closest, intersections), `quantity_takeoff` (số lượng / chiều dài / diện tích theo layer, loại, block; hệ số đổi đơn vị; CSV), `extract_block_data` (mỗi insert 1 dòng: vị trí, attribute, dynamic property; CSV) |
| Vẽ / sửa | `add_entities` (line, polyline, rectangle, circle, arc, ellipse, point, text, mtext, attdef, insert + attribute, hatch, spline, table, mleader, dim_aligned, dim_linear, dim_radius, dim_diameter, dim_angular, dim_arc, dim_ordinate, dim_chain nối tiếp / baseline; text / mtext có `field`: tên file, ngày, diện tích / chiều dài đối tượng), `update_entities` (cả `dynamic_props`, `points` của polyline, `cells` của table, `draw_order`), `delete_entities`, `transform_entities` (move/copy/rotate/scale/mirror/align, array lưới `array_rect`, array tròn `array_polar`), `create_block`, `explode_entities` (attribute → text giữ giá trị) |
| Sửa hàng loạt | `replace_text` (TEXT, MTEXT, attribute, dim, mleader, ô table; regex; `dry_run`), `edit_geometry` (offset, join, boundary, extend, trim, break, fillet, chamfer), `sync_block_attributes` (như ATTSYNC), `update_fields` (như UPDATEFIELD) |
| Sửa trong block | `add_entities` / `query_entities` / `get_texts` với `space: "block:TÊN"`, rồi `update_entities` / `delete_entities` theo handle: sửa định nghĩa block không cần BEDIT (mọi insert đổi theo) |
| Xem | `zoom`, `screenshot` (ảnh PNG cho Claude xem) |
| Lệnh | `run_command` (gửi dòng lệnh AutoCAD, chờ xong, báo đối tượng mới tạo, tự Esc nếu lệnh còn treo), `cancel_command`, `reload_addin` |
| Bridge | `list_autocad_instances`, `switch_autocad_instance` |

Tạo / đổi tên / xoá theo loại đối tượng:

| Loại | Tạo | Đổi tên | Xoá |
|---|---|---|---|
| File bản vẽ | `new_document save_as`, `save_document` | `rename_document` | `delete_drawing_file` |
| Layer | `set_layer`, `import_from_drawing` | `set_layer new_name` / `rename_object` | `delete_layer` / `delete_object` / `purge` |
| Block | `create_block` (attribute: `add_entities type=attdef` trước), `add_entities insert block_file`, `import_from_drawing` | `rename_object` | `delete_object` (`erase_references`) / `purge` |
| Layout | `create_layout` | `rename_object` | `delete_object` |
| Text / dim style | `set_text_style`, `set_dim_style`, `import_from_drawing` | `new_name` / `rename_object` | `delete_object` / `purge` |
| Linetype | `load_linetype`, `import_from_drawing` | `rename_object` | `delete_object` / `purge` |
| View | `save_view` | `rename_object` | `delete_object` |
| UCS | `set_ucs save` | `rename_object` | `delete_object` |
| Page setup | `set_page_setup` | `rename_object` | `delete_object` |
| Layer state | `layer_state save` / `import` | `layer_state rename` | `layer_state delete` |
| Group | `set_group` | `rename_object` | `delete_object` (ungroup) |
| Xref | `attach_xref` | — (không đổi tên xref: layer/style của nó giữ tên cũ) | `manage_xref detach` |
| Entity | `add_entities` | — | `delete_entities` |

Xuất file:

| Định dạng | Tool |
|---|---|
| PDF / DWFx / DWF theo layout của bản vẽ đang mở | `export_pdf` (1 file nhiều trang, hoặc `separate_files=true`) |
| 1 PDF gộp nhiều bản vẽ (kể cả file đang đóng) | `publish_pdf` |
| DXF | `save_document path=...dxf version=2018` |
| DWG bản cũ / bản sao | `save_document version=2013`, `copy=true` |
| PNG khung nhìn hiện tại | `screenshot save_path=...png` |
| DWG mới từ entity / block (WBLOCK) | `export_entities` |
| Layer state .las | `layer_state action=export` |
| CSV cho Excel (UTF-8, giữ tiếng Việt) | `quantity_takeoff csv_path=...`, `extract_block_data csv_path=...` (`delimiter=";"` nếu Excel dùng dấu phẩy thập phân) |

## Ví dụ yêu cầu cho Claude

| Bạn nói | Claude dùng |
|---|---|
| "Mở file A-101.dwg trong thư mục dự án" | `list_drawing_files` → `open_document` |
| "Xuất tất cả layout ra 1 file PDF khổ A3, in đen trắng" | `export_pdf layouts=["*"] paper=A3 plot_style=monochrome.ctb` |
| "Gộp layout của 3 bản vẽ này vào 1 PDF" | `publish_pdf drawings=[...]` |
| "Đổi tên layer A-WALL thành A-TUONG, xoá layer không dùng" | `rename_object kind=layer` → `purge kinds=["layer"]` |
| "Tạo text style Arial 2.5 và dim style tỉ lệ 1:100" | `set_text_style` → `set_dim_style props={DIMSCALE: 100, ...}` |
| "Tạo layout A1-01, đặt viewport nhìn vào phòng khách, tỉ lệ 1:50" | `create_layout` → `add_viewport scale=1:50 view_window=...` |
| "Liệt kê các block cửa và số lượng" | `list_blocks name=DOOR*` hoặc `query_entities types=["INSERT"] block_names=["DOOR*"]` |
| "Lưu bản sao 2013 gửi khách hàng" | `save_document copy=true version=2013 path=...` |
| "Attach file nền kiến trúc làm xref" | `attach_xref relative_path=true` |
| "Tính tổng diện tích hatch trên layer A-SAN, đổi ra m²" | `quantity_takeoff layers=["A-SAN"] types=["HATCH"] area_factor=0.000001` |
| "Xuất danh sách cửa và thuộc tính ra Excel" | `extract_block_data block_names=["DOOR*"] csv_path=...` |
| "Đổi mọi chữ 'P.KHÁCH' thành 'PHÒNG KHÁCH'" | `replace_text dry_run=true` (xem trước) → `replace_text` |
| "Offset đường bao này 200 ra phía ngoài" | `edit_geometry operation=offset distance=200 side_point=...` |
| "Lấy khung tên mới từ file mẫu, cập nhật mọi bản" | `import_from_drawing kind=block replace=true` → `sync_block_attributes` |
| "Khoá viewport, freeze layer nội thất trong viewport đó" | `set_viewport locked=true freeze_layers=["I-*"]` |
| "Đo khoảng cách giữa 2 trục, diện tích phòng này" | `measure mode=distance` / `measure mode=area handles=[...]` |
| "Bo góc R500 cho 2 tường này, cắt phần thừa của đường này" | `edit_geometry operation=fillet radius=500` / `operation=trim` |
| "Rải 5 × 8 cột, bước 6000" | `transform_entities operation=array_rect rows=5 columns=8 ...` |
| "Ghi diện tích phòng tự cập nhật" | `add_entities type=text field={type: area, handle: ..., factor: 0.000001}` → `update_fields` |
| "Sửa khung tên trong block TITLE, thêm logo" | `add_entities space="block:TITLE"` |
| "Lưu trạng thái layer hiện tại, tắt layer nội thất" | `layer_state action=save` → `set_layer` |
| "Tạo page setup A1 1:100 cho mọi layout" | `set_page_setup paper=A1 scale=1:100 apply_to=["*"]` |
| "Chèn file scan .rcp, đặt UCS theo tường" | `attach_point_cloud` → `set_ucs action=set` |
| "Cao độ surface EG tại các điểm này" (Civil 3D) | `civil_query elevation_at={surface: EG, points: [...]}` |

## Ribbon: Add-ins › T3Lab MCP

Cùng nút, thứ tự, icon (T3 Space Line của t3lab-revit-api, theo `COLORTHEME`) và câu chữ với panel của [T3Lab Revit MCP](https://github.com/thanhtranarch/t3lab_mcp-revit) trên tab Add-Ins của Revit.

| Nút | Lệnh | Việc |
|---|---|---|
| **Start / Stop** | `T3MCP` | Bật/tắt server MCP. Tắt thì lần mở AutoCAD sau cũng không tự chạy (file `%APPDATA%\T3LabCAD\autostart.off`) tới khi bấm Start (`T3MCPSTART` / `T3MCPSTOP` vẫn dùng được) |
| **Agent Cursors** | `T3CURSORS` | Bật/tắt bảng theo dõi AI (server đang tắt thì bật luôn server) |
| **Connect AI** | `T3MCPCONNECT` | Thêm AutoCAD vào mọi AI app trên máy (Claude Desktop, Claude Code, Codex, Cursor, VS Code, Gemini CLI, Windsurf, Antigravity), hoặc chọn *Check* để chỉ kiểm tra. Chạy `register_mcp.py` (giống của Revit) trong một cửa sổ console |
| **Status** · **Reload** | `T3MCPSTATUS` · `T3MCPRELOAD` | Port, tool, số lần gọi, hàng đợi, Agent Cursors, log · nạp lại `T3LabCAD.Core.dll` sau khi cập nhật |

## Agent Cursors — xem AI đang làm gì

Giống hệt Agent Cursors của T3Lab Revit MCP (cùng bố cục, chữ, màu, nút): bảng hoạt động cho biết AI app nào đang làm việc trong AutoCAD, nó định làm gì và đang ở bước nào. Mặc định **tắt**. Bật / tắt bằng nút **Agent Cursors** (Add-ins › T3Lab MCP) hoặc lệnh `T3CURSORS`. Khi AI gọi tool lần đầu mà bảng đang tắt, dòng lệnh nhắc gõ `T3CURSORS`.

- **Bảng hoạt động** (palette, dock trái / phải, sáng / tối theo `COLORTHEME`): mỗi AI app (tên lấy từ MCP `clientInfo`: Claude, Claude Code, Cursor, ChatGPT...): con trỏ màu, tên, trạng thái, số lần gọi · số thay đổi, mục tiêu, kế hoạch *Step 2/5* + thanh tiến độ + ✓ ▸ ○, dải màu của việc đang chạy (đọc, tạo, sửa, xoá, file, xem, lệnh); rồi **RECENT**: 6 bước gần nhất (✓ ⏸ – ✕, "Claude · 12 s ago", số thay đổi · giây hoặc lỗi; "–" = không chạy vì AutoCAD bận). Trạng thái: *In line · 1 ahead* (xếp hàng sau AI khác, dải vàng) · *Waiting for AutoCAD* (tới lượt nhưng AutoCAD đang mở dialog) · *Working 12 s* · *Thinking ···* (vừa xong một bước, chờ model, 20 s) · *Idle · last step 3 min ago*.
- **Mỗi chat một agent:** hai cửa sổ Claude Code là "Claude Code" và "Claude Code 2", hai màu, hai con trỏ.
- **Nút** (cùng thứ tự với Revit): *Pause / Resume* (AI chỉ được đọc / xem; tool sửa bản vẽ hoặc ghi file bị từ chối kèm lời nhắn cho AI, kể cả lệnh đang chờ) · *Select changes* (chọn và zoom tới đối tượng AI đã tạo / sửa) · *Replay* (chiếu lại các bước trên bản vẽ) · *Clear*.
- **Show on drawing** (mặc định bật, như Revit): con trỏ của từng AI lướt tới chỗ đang làm, khung xanh lá = tạo mới, xanh dương = sửa, dấu × đỏ = xoá, xám = đọc (kèm đối tượng + layer). Chỉ là hình tạm (transient), không vào bản vẽ, không vào Undo, không in.
  - Tag con trỏ nền pastel + chữ đậm màu, ghi **thứ vừa làm**: "Claude · Line 3450", "Circle Ø1000", "Block Door-900", "Line ×4"; lúc chạy ghi việc đang làm, giữa hai bước ghi "thinking ···".
  - **Vẽ theo (draw-along):** line, polyline, arc, circle mới tạo → con trỏ chạy dọc từ đầu tới cuối, kèm một vệt màu.
  - Hai AI cùng trỏ một chỗ thì đứng lệch nhau.
- **Follow:** tự zoom tới vùng AI vừa làm (như Follow của Revit).
- AI có thể gọi `t3lab_narrate` (goal, tasks, step, done) để bảng hiện kế hoạch; không gọi thì bảng vẫn hiện từng tool.
- Lựa chọn lưu ở `%APPDATA%\T3LabCAD\agent_cursors.json`. Nút X chỉ đóng bảng: `T3CURSORS` mở lại, gõ khi bảng đang mở thì tắt hẳn. Bước đọc / xem chỉ di chuyển con trỏ (không khung, không zoom). "Select changes" chỉ chọn đối tượng AI đã sửa trong bản vẽ và space đang mở. Mọi lỗi của phần hiển thị chỉ ghi log, không làm hỏng lời gọi tool.

Ý tưởng con trỏ nhiều agent tham khảo từ [swarm-cursors](https://github.com/thanhtranarch/swarm-cursors) (chỉ lấy ý tưởng, không dùng mã).

## Nhiều AI cùng lúc · tuỳ chọn bridge

AutoCAD chỉ chạy một lời gọi một lúc. Add-in xếp hàng công bằng (`Conductor.cs`): người đang chat trước bot, rồi xoay vòng theo từng agent (một agent gọi dồn dập không chặn agent khác). Lời gọi chờ quá hạn (`queue_wait_s`, mặc định 45 s) thì **không chạy** và trả về `{"error_code": "autocad_busy", "nothing_ran": true, "retry_after_s": …, "running": {...}}`, nên không có thay đổi nào xảy ra sau khi AI đã bỏ cuộc. `cancel_command` và `t3lab_narrate` không phải xếp hàng. `T3MCPSTATUS` và `health` cho thấy ai đang giữ AutoCAD và bao nhiêu lời gọi đang chờ.

Tuỳ chọn thêm vào `args` của server `t3lab-autocad` trong config AI app (hoặc biến môi trường):

| Tuỳ chọn | Biến | Việc |
|---|---|---|
| `--tier=read` · `trusted` · `experimental` | `T3LAB_TIER` | `read`: chỉ tool đọc · `trusted`: thêm tool sửa bản vẽ / ghi file · `experimental` (mặc định): thêm `run_command`. Tool vượt tier bị ẩn và bị từ chối (`tier_denied`) trước khi tới AutoCAD |
| `--deny=a,b` | `T3LAB_DENY` | Tắt vài tool cho client này (`tool_denied`) |
| `--agent=<tên>` | `T3LAB_AGENT` | Tên hiện trên Agent Cursors (mặc định: tên app) |
| `--lane=person` · `task` · `auto` | `T3LAB_LANE` | Thứ tự hàng đợi (bot tự chạy: `auto`). Chỉ xếp thứ tự, không cấp quyền |
| `--queue-wait=45` | `T3LAB_QUEUE_WAIT_S` | Thời gian chờ tối đa (giây) để tới lượt |
| `--audit=0` | `T3LAB_AUDIT=0` | Tắt nhật ký `%APPDATA%\T3LabCAD\audit\<ngày>.jsonl` (mỗi lời gọi một dòng: agent, tool, ok / error_code, thời gian; không ghi tham số) |

Lỗi có `error_code` là JSON `{error_code, message, retryable, nothing_ran, hint}`: `autocad_busy`, `paused_by_user` (add-in); `tier_denied`, `tool_denied`, `autocad_not_running`, `timeout`, `bridge_error` (bridge).

## An toàn

- Server chỉ nghe `127.0.0.1`.
- `run_command` từ chối AutoLISP (`(`, `!`) và các lệnh nạp / chạy code hoặc mở chương trình khác (SHELL, NETLOAD, APPLOAD, SCRIPT, VBARUN...).
- Tool ghi bị từ chối khi người dùng đang ở giữa một lệnh.
- `close_document` không đóng bản vẽ chưa lưu trừ khi `save=true` hoặc `discard_changes=true`; `rename_document` cũng vậy (`save_changes=true`).
- `delete_drawing_file` chỉ xoá file AutoCAD (.dwg, .dxf, .dwt, .dws, .bak, .sv$) không đang mở, mặc định vào Recycle Bin; ổ mạng / USB phải `permanent=true`.
- `delete_object` / `purge` không xoá đối tượng còn đang dùng, layer 0, style Standard, đối tượng current, Model hay layout giấy cuối cùng; `purge dry_run=true` chỉ báo cáo. Xoá nhiều tên cùng lúc: mỗi tên thành công / thất bại riêng.
- Không đổi tên / xoá layer, style của xref (tên dạng `XREF|NAME`); xref thì dùng `manage_xref`.
- `export_entities` không ghi đè file có sẵn trừ khi `overwrite=true`, không ghi vào bản vẽ đang mở.
- `space: "block:TÊN"` không sửa xref, layout hay block ẩn danh (dynamic block / dim); sửa block thì mọi insert của nó đổi theo.
- Array tối đa 5 000 bản sao / lần.
- `replace_text` bỏ qua đối tượng trên layer bị khoá (có đếm); MTEXT có định dạng cắt ngang chữ cần tìm thì chỉ báo, không sửa. `dry_run=true` để xem trước.
- `sync_block_attributes` giữ giá trị attribute; vị trí / kiểu chữ theo định nghĩa block, trừ khi `keep_positions=true`. Attribute không còn trong định nghĩa bị xoá (`remove_extra=false` để giữ).
- `import_from_drawing replace=true` định nghĩa lại block / layer / style đã có (đánh dấu destructive), mọi insert của block bị thay đổi theo.
- `quantity_takeoff` / `extract_block_data` ghi đè file CSV cũ (báo lỗi nếu file đang mở trong Excel).
- `export_pdf` / `publish_pdf` ghi đè file PDF cũ (báo lỗi nếu file đang mở trong trình xem PDF), tạm đặt `BACKGROUNDPLOT=0` rồi trả lại, và trả về layout đang mở như trước.
- Text, tên layer, block, attribute là dữ liệu do người vẽ nhập: Claude được dặn coi đó là dữ liệu, không phải lệnh.

## Phát triển

Mã nguồn tool (`src/T3LabCAD.Core/Tools/`):

| File | Tool |
|---|---|
| `StatusTools.cs` | trạng thái, system variable |
| `DocumentTools.cs` | mở / tạo / lưu / đóng / chuyển bản vẽ, list / activate layout |
| `FileTools.cs` | tìm / đổi tên / xoá file bản vẽ |
| `LayoutTools.cs` | tạo layout, thêm / sửa viewport |
| `ExportTools.cs` | `export_pdf` (PlotEngine), `publish_pdf` (Publisher + DSD) |
| `LayerTools.cs` | layer |
| `StyleTools.cs` | text style, dim style, linetype |
| `NamedObjectTools.cs` | list / rename / delete / purge mọi loại đối tượng có tên, group |
| `ImportTools.cs` | `import_from_drawing` |
| `QueryTools.cs` | truy vấn entity, text, block, selection |
| `MeasureTools.cs` | `measure`, `quantity_takeoff`, `extract_block_data` |
| `EditTools.cs` | vẽ, sửa, xoá, transform, block, explode |
| `Annotation.cs` | các kiểu `add_entities`: table, mleader, dim radius / diameter / angular / arc / ordinate / chain |
| `ModifyTools.cs` | `replace_text`, `edit_geometry`, `sync_block_attributes` |
| `GeometryOps.cs` | extend, trim, break, fillet, chamfer (cho `edit_geometry`) |
| `FieldTools.cs` | field trong text / mtext, `update_fields` |
| `SheetTools.cs` | `set_page_setup`, `layer_state` |
| `ReferenceTools.cs` | `attach_underlay`, `export_entities` (WBLOCK), `set_ucs` |
| `CivilTools.cs` | `attach_point_cloud`, `civil_query`, `civil_points` (Civil 3D qua reflection: không cần tham chiếu DLL Civil 3D, AutoCAD thường vẫn nạp được) |
| `XrefTools.cs` | xref |
| `ViewTools.cs` | zoom, screenshot, named view |
| `CommandTools.cs` | `run_command`, `cancel_command`, `reload_addin` |

Agent Cursors (`src/T3LabCAD.Core/`): `AgentCursors.cs` (theo dõi bước, tên AI, `t3lab_narrate`, cài đặt), `ActivityPalette.cs` (bảng hoạt động), `CursorOverlay.cs` (con trỏ + khung trên bản vẽ bằng TransientManager). Hook trong `Rpc.CallTool`.

Hàng đợi (`src/T3LabCAD.Core/Conductor.cs`): một slot AutoCAD cho mọi AI app, không dùng AutoCAD API, test riêng không cần AutoCAD: `dotnet run --project tests/conductor_test`.

Dùng chung (`src/T3LabCAD.Core/`): `Acad.cs` (space, layer, màu...), `Describe.cs` (entity → JSON), `Metrics.cs` (length / area), `Csv.cs` (CSV UTF-8 cho Excel).

Thêm tool: tạo method trong một file trên và `Registry.Add(...)` trong `Register()` (schema viết bằng helper `Sch`); file mới thì thêm `XxxTools.Register()` vào `Registry.Build()`. Chọn `ToolKind`:
- `Read` — chỉ đọc, có transaction sẵn (`ctx.Tr`).
- `Write` — khoá document, transaction tự commit khi trả về / rollback khi lỗi = 1 bước Undo. `tx: false` khi tool tự mở transaction (đổi layout, plot, xref reload...).
- `App` — mở/đóng/chuyển bản vẽ, chạy trong application context của AutoCAD (`ExecuteInApplicationContext`). Lưu ý: code chạy ở document context mà đổi bản vẽ active sẽ bị treo tới khi bản vẽ cũ được active lại.
- `background: true` — chạy trên luồng socket, tự gọi `Entry.Main.Run` khi cần AutoCAD (`run_command`, `list_drawing_files`).
- `writesFiles: true` — tool `Read` có thể ghi file (CSV): không khai báo là read-only với Claude.

`bridge/tools_snapshot.json` là danh sách tool gửi cho Claude khi AutoCAD chưa mở. Sau khi thêm/sửa tool, tạo lại từ mã nguồn (không cần AutoCAD; CI kiểm tra bằng `--check`). Script đọc trực tiếp các lời gọi `Registry.Add` nên mô tả tool phải là chuỗi literal nối bằng `+`, hằng `const string` hoặc `static readonly string[]`, không dùng `$"..."`:

```bash
python -I scripts/gen_tools_snapshot.py
```

Cập nhật khi AutoCAD đang mở:

```bash
powershell -ExecutionPolicy Bypass -File scripts/install.ps1
```

rồi gõ `T3MCPRELOAD` trong AutoCAD (hoặc gọi tool `reload_addin`). Riêng `T3LabCAD.Loader.dll` bị AutoCAD khoá: đổi Loader thì phải khởi động lại AutoCAD.

Test:

```bash
python -I tests/smoke_bridge.py
```

```bash
python -I tests/smoke_live.py
```

- `smoke_bridge.py` chạy không cần AutoCAD (dùng `tests/mock_autocad.py`); kiểm tra cả danh sách tool offline khớp snapshot.
- `smoke_live.py` cần AutoCAD đang chạy (Windows). Chỉ **đọc** trên bản vẽ bạn đang mở. Mọi thao tác ghi làm trên một bản vẽ mới: tạo / đổi tên / xoá layout, style, linetype, block, group, view; explode; purge; đo (diện tích hình chữ nhật 1000 × 500 = 500 000, offset 100 ra ngoài = 840 000), join, boundary, giao điểm; table, mleader, 5 kiểu dim + dim_chain; `replace_text` (tiếng Việt, mọi kiểu chữ hoa / thường); block có attribute + `sync_block_attributes`; `set_viewport`; và kiểm tra 1 lần gọi = 1 bước Undo (`_.U`). Import layer từ file khác và CSV cũng chạy trong thư mục tạm. Phần file (lưu, bản sao 2013, xref, đổi tên, xoá, export / publish PDF) làm trong thư mục tạm `%TEMP%\t3labcad_live_<pid>`, xoá khi xong. Bản 1.4: extend (500 → 1000), trim, break, fillet R200 (2 line 800 + cung 314.1593), chamfer 100 (141.4214), fillet mọi góc polyline (diện tích 491 415.9265), align, array lưới / tròn, giới hạn 5 000 bản sao, sửa trong `block:TÊN`, field diện tích, `t3lab_narrate`, layer state (save / export .las / rename / restore / import), page setup, UCS, WBLOCK, underlay ảnh PNG. Cuối cùng đóng bản vẽ thử không lưu và kiểm tra `reload_addin`.

Log: `%APPDATA%\T3LabCAD\logs\t3labcad.log`.

## Phát hành

`.github/workflows/release.yml` build 3 series trên Ubuntu, chạy `python scripts/package.py` và đưa `T3LabCAD.zip` lên GitHub Release `v<Version>` (`src/Directory.Build.props`). Chạy mỗi khi merge vào `main`, khi push tag `v*`, hoặc bấm **Run workflow** (Actions › Release). Cùng version thì file zip được thay; đổi `<Version>` để ra release mới (cả `src/Directory.Build.props`, `AppVersion` trong `bundle/PackageContents.xml` và `Entry.Version`). MCP Control trong T3Lab tải bản mới nhất từ `releases/latest/download/T3LabCAD.zip`.

## Lịch sử phiên bản

| Version | Nội dung |
|---|---|
| 1.5.0 | Đồng bộ với T3Lab Revit MCP: cùng ribbon (Start / Stop · Agent Cursors · Connect AI · Status / Reload, icon T3 Space Line), cùng bảng Agent Cursors (bố cục, chữ, màu `T3Style`, nút Pause · Select changes · Replay · Clear, Show on drawing · Follow), cùng dòng Status. Mới: `T3MCP` (Start / Stop, tắt thì giữ tắt), `T3MCPCONNECT` (Connect AI cho 8 AI app, `register_mcp.py` mới). Ribbon: panel **T3Lab MCP** nằm trong tab **Add-ins** của AutoCAD, không còn tab "T3Lab" riêng. Hàng đợi công bằng cho nhiều AI (`Conductor.cs`, `autocad_busy` thay vì chạy muộn), lỗi dạng JSON có `error_code`. Bridge 1.1.0: `--tier` / `--deny` / `--agent` / `--lane` / `--queue-wait`, nhật ký audit, mỗi chat một agent. Agent Cursors: trạng thái in line / waiting for AutoCAD / thinking / last step ago, tag pastel ghi đối tượng ("Line 3450"), vẽ theo line / polyline / arc mới, con trỏ không đè nhau. |
| 1.4.0 | 55 → 65 tool. `edit_geometry` thêm extend, trim, break, fillet, chamfer. `space: "block:TÊN"` sửa định nghĩa block. `transform_entities` thêm align, array_rect, array_polar. Field trong text / mtext + `update_fields`. `set_page_setup`, `layer_state`, `attach_underlay`, `export_entities` (WBLOCK), `set_ucs`, `attach_point_cloud`, `civil_query`, `civil_points` (Civil 3D). **Agent Cursors**: bảng hoạt động + con trỏ AI trên bản vẽ (`T3CURSORS`), `t3lab_narrate`. |
| 1.3.0 | 47 → 55 tool. `measure`, `quantity_takeoff`, `extract_block_data` (CSV), `replace_text`, `edit_geometry` (offset, join, boundary), `sync_block_attributes`, `import_from_drawing`, `set_viewport`. `add_entities` thêm table, mleader, attdef, dim radius / diameter / angular / arc / ordinate / chain. `update_entities` thêm dynamic block property, đỉnh polyline, ô table, draw order. `query_entities` thêm length / area, lọc màu / linetype / closed, `totals`. |
| 1.2.0 | 27 → 47 tool. File (`list_drawing_files`, `rename_document`, `delete_drawing_file`; `save_document` bản sao / bản cũ; `new_document save_as`), layout + viewport, text / dim style, linetype, `list_named_objects` / `rename_object` / `delete_object` / `purge`, group, named view, xref, `explode_entities`, `export_pdf`, `publish_pdf`. `scripts/gen_tools_snapshot.py` + kiểm tra trong CI. |
| 1.1.2 | Icon ribbon T3Lab không bị cắt; nút play / stop đặc. |
| 1.1.1 | Icon ribbon theo giao diện sáng / tối của AutoCAD. |
| 1.1.0 | Hỗ trợ AutoCAD / Civil 3D 2022–2027 (R24, R25, R26); file `T3LabCAD.zip` cho MCP Control; release mỗi khi merge vào `main`. |
| 1.0.0 | Add-in AutoCAD + bridge stdio, 27 tool. |

## Dự kiến (chưa làm)

Đợt 2–7 đã xong trong 1.4.0. Tiếp theo (theo thứ tự):

1. Chuyển chữ TCVN3 / VNI sang Unicode (`convert_text_encoding`; `get_texts` tự giải mã).
2. `audit_drawing` / `clean_drawing` (AUDIT, purge sâu, xoá đối tượng rỗng / trùng).
3. `layer_standard`: kiểm tra / áp chuẩn layer từ CSV.
4. `tag_areas` (ghi diện tích phòng), hatch theo điểm chọn, sửa hatch.
5. `table_data`: CSV ↔ table.
6. `title_blocks`: điền / đọc khung tên hàng loạt; `create_layout` từ file mẫu.
7. Point cloud: crop / slice; `rectify_geometry` (nắn thẳng, vuông góc).
8. `entity_data`: xdata / xrecord; lọc theo xdata trong `query_entities`.
9. `coordinate_transform` (ma trận, Helmert), `survey_points`.
10. `draw_grid` (lưới trục), dim_chain theo polyline, `text_tools`.
11. `inspect_drawing_file` (đọc file đang đóng), publish cả thư mục, `export_pdf` ra PNG.
12. `compare_drawings`, revision cloud.
13. `solid_ops` (3D solid), đo khối lượng, view 3D.
