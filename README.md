# T3Lab CAD MCP — add-in AutoCAD cho Claude

Add-in AutoCAD (giống T3Lab Revit MCP) cho phép Claude Desktop / Claude Code làm việc với **bản vẽ đang mở trong AutoCAD**:

- **Đọc:** truy vấn đối tượng (kèm length / area), text, attribute, layer, block, layout, style, xref; chụp màn hình để Claude xem.
- **Đo / bóc khối lượng:** khoảng cách, góc, diện tích, giao điểm; tổng số lượng / chiều dài / diện tích theo layer, loại, block; xuất dữ liệu block ra CSV cho Excel.
- **Vẽ / sửa:** vẽ 23 loại đối tượng (cả table, multileader, attribute definition, 8 kiểu dim), sửa thuộc tính (cả dynamic block, đỉnh polyline, ô table), move / copy / rotate / scale / mirror, offset / join / boundary, tìm và thay text, tạo block, đồng bộ attribute, explode.
- **Quản lý:** tạo / đổi tên / xoá layer, block, layout, viewport, text style, dim style, linetype, view, group, xref; purge; lấy block / layer / style từ file khác.
- **File:** mở, tạo, lưu (kể cả bản cũ 2018…2000), lưu bản sao, đổi tên, xoá file bản vẽ; duyệt thư mục.
- **Xuất:** PDF / DWFx / DWF theo layout (`export_pdf`), publish nhiều bản vẽ vào 1 PDF (`publish_pdf`), DXF.
- **Lệnh:** chạy lệnh AutoCAD dạng dòng lệnh khi chưa có tool riêng.

Tổng cộng **57 tool**: 55 tool chạy trong AutoCAD + 2 tool của bridge. Mỗi thay đổi là 1 bước Undo.

```
Claude ──stdio MCP──> bridge.py (%APPDATA%\T3LabCAD) ──TCP 127.0.0.1:48900-48908──> T3LabCAD add-in trong AutoCAD
```

| Phần | Vai trò |
|---|---|
| `src/T3LabCAD.Loader` | DLL AutoCAD load (bundle autoloader). Lệnh `T3MCPSTART / T3MCPSTOP / T3MCPSTATUS / T3MCPRELOAD / T3CURSORS`, tab ribbon **T3Lab › MCP Server**. Nạp Core từ bytes nên Core thay được khi AutoCAD đang chạy. |
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
4. Thêm server `t3lab-autocad` vào `claude_desktop_config.json` (có backup). Thêm `-ClaudeCode` để đăng ký cho Claude Code CLI (`claude mcp add --scope user`).

Sau đó **khởi động lại Claude Desktop**. Trong AutoCAD gõ `T3MCPSTATUS` để kiểm tra:
`T3LabCAD MCP v1.3.0: listening on 127.0.0.1:48900 | 55 tools | ...`

Nếu AutoCAD đang mở lúc cài lần đầu: khởi động lại AutoCAD, hoặc `NETLOAD` file `T3LabCAD.Loader.dll` trong thư mục bundle.

## Tool

| Nhóm | Tool |
|---|---|
| Trạng thái | `get_status`, `get_sysvars`, `set_sysvar` |
| Bản vẽ | `open_document` (tên không có thư mục = cạnh bản vẽ đang mở), `new_document` (`save_as`), `activate_document`, `save_document` (dwg / xuất dxf, `copy=true` lưu bản sao, `version` 2018/2013/2010/2007/2004/2000), `close_document` |
| File | `list_drawing_files` (duyệt thư mục, đánh dấu file đang mở), `rename_document` (đổi tên file, bản vẽ đang mở được đóng → đổi tên → mở lại), `delete_drawing_file` (vào Recycle Bin, trừ khi `permanent=true`) |
| Layout | `list_layouts`, `activate_layout`, `create_layout` (mới hoặc copy từ layout khác), `add_viewport` (vùng model + tỉ lệ 1:100), `set_viewport` (tỉ lệ, tâm, khoá, on/off, freeze layer riêng trong viewport) |
| Export / publish | `export_pdf` (plot layout của bản vẽ đang mở ra PDF / DWFx / DWF: 1 file nhiều trang hoặc mỗi layout 1 file; đổi khổ giấy, ctb, vùng in, tỉ lệ; `list_options` liệt kê máy in, khổ giấy, ctb), `publish_pdf` (gộp layout của nhiều bản vẽ, kể cả file đang đóng, vào 1 PDF theo page setup). DXF: `save_document path=*.dxf`; ảnh PNG: `screenshot` |
| Layer | `list_layers`, `set_layer` (tạo/sửa/đổi tên/on/freeze/lock/current), `delete_layer` (có `merge_into`) |
| Style / linetype | `set_text_style` (font TrueType hoặc SHX, height, width factor, oblique), `set_dim_style` (tạo từ style gốc, đặt mọi biến DIM*, tự cập nhật dim), `load_linetype` (từ acadiso.lin, có wildcard) |
| Đối tượng có tên | `list_named_objects`, `rename_object`, `delete_object`, `purge` — cho layer, block, layout, text_style, dim_style, linetype, view, ucs, group, mleader_style, table_style (xref chỉ list); `import_from_drawing` (lấy block / layer / style / linetype từ .dwg / .dwt khác, `replace=true` định nghĩa lại) |
| Group / view / xref | `set_group` (tạo, thêm/bớt entity), `save_view`, `restore_view`, `attach_xref` (attach/overlay, đường dẫn tương đối), `manage_xref` (detach/reload/unload/bind/insert/repath) |
| Đọc | `query_entities` (lọc theo type, layer, block, text, màu, linetype, closed, vùng chọn, handle; `totals` cộng length / area; `detail=full` có length, area, ô table; phân trang), `get_texts` (TEXT/MTEXT/ATTRIB/MLEADER), `list_blocks`, `selection` |
| Đo / bóc khối lượng | `measure` (distance, angle, area, length, bbox, closest, intersections), `quantity_takeoff` (số lượng / chiều dài / diện tích theo layer, loại, block; hệ số đổi đơn vị; CSV), `extract_block_data` (mỗi insert 1 dòng: vị trí, attribute, dynamic property; CSV) |
| Vẽ / sửa | `add_entities` (line, polyline, rectangle, circle, arc, ellipse, point, text, mtext, attdef, insert + attribute, hatch, spline, table, mleader, dim_aligned, dim_linear, dim_radius, dim_diameter, dim_angular, dim_arc, dim_ordinate, dim_chain nối tiếp / baseline), `update_entities` (cả `dynamic_props`, `points` của polyline, `cells` của table, `draw_order`), `delete_entities`, `transform_entities` (move/copy/rotate/scale/mirror), `create_block`, `explode_entities` (attribute → text giữ giá trị) |
| Sửa hàng loạt | `replace_text` (TEXT, MTEXT, attribute, dim, mleader, ô table; regex; `dry_run`), `edit_geometry` (offset, join, boundary), `sync_block_attributes` (như ATTSYNC) |
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

## Agent Cursors — xem AI đang làm gì

Giống Agent Cursors của T3Lab Revit: một bảng hoạt động cho biết AI app nào đang làm việc trong AutoCAD, nó định làm gì và đang ở bước nào. Mặc định **tắt**. Bật / tắt bằng lệnh `T3CURSORS` hoặc nút **Agent Cursors** trên ribbon (T3Lab › MCP Server). Khi AI gọi tool lần đầu mà bảng đang tắt, dòng lệnh nhắc gõ `T3CURSORS`.

- **Bảng hoạt động** (palette, dock trái / phải): mỗi AI app 1 dòng (tên lấy từ MCP `clientInfo`: Claude, Claude Code, Cursor, ChatGPT...), trạng thái "Working 12 s", mục tiêu, kế hoạch ✓ / ▶ / ○, bước đang chạy theo màu (đọc, tạo, sửa, xoá, file, xem, lệnh) và 20 bước gần nhất (thời gian, số đối tượng tạo / sửa / xoá, lỗi).
- **Nút:** *Pause* (AI chỉ được đọc; tool ghi bị từ chối kèm lời nhắn cho AI), *Select touched* (chọn đối tượng AI vừa đụng tới), *Zoom*, *Replay* (chiếu lại các bước trên bản vẽ), *Clear*.
- **Show on the drawing:** con trỏ của từng AI lướt tới chỗ đang làm, khung xanh = tạo mới, khung màu AI = sửa, dấu × đỏ = xoá (kèm loại + layer). Chỉ là hình tạm (transient), không vào bản vẽ, không vào Undo, không in.
- **Zoom to the work:** tự zoom tới vùng AI vừa làm.
- AI có thể gọi `t3lab_narrate` (goal, tasks, step, done) để bảng hiện kế hoạch; không gọi thì bảng vẫn hiện từng tool.
- Lựa chọn lưu ở `%APPDATA%\T3LabCAD\agent_cursors.json`. Đóng palette = tắt Agent Cursors. Mọi lỗi của phần hiển thị chỉ ghi log, không làm hỏng lời gọi tool.

Ý tưởng con trỏ nhiều agent tham khảo từ [swarm-cursors](https://github.com/thanhtranarch/swarm-cursors) (chỉ lấy ý tưởng, không dùng mã).

## An toàn

- Server chỉ nghe `127.0.0.1`.
- `run_command` từ chối AutoLISP (`(`, `!`) và các lệnh nạp / chạy code hoặc mở chương trình khác (SHELL, NETLOAD, APPLOAD, SCRIPT, VBARUN...).
- Tool ghi bị từ chối khi người dùng đang ở giữa một lệnh.
- `close_document` không đóng bản vẽ chưa lưu trừ khi `save=true` hoặc `discard_changes=true`; `rename_document` cũng vậy (`save_changes=true`).
- `delete_drawing_file` chỉ xoá file AutoCAD (.dwg, .dxf, .dwt, .dws, .bak, .sv$) không đang mở, mặc định vào Recycle Bin; ổ mạng / USB phải `permanent=true`.
- `delete_object` / `purge` không xoá đối tượng còn đang dùng, layer 0, style Standard, đối tượng current, Model hay layout giấy cuối cùng; `purge dry_run=true` chỉ báo cáo. Xoá nhiều tên cùng lúc: mỗi tên thành công / thất bại riêng.
- Không đổi tên / xoá layer, style của xref (tên dạng `XREF|NAME`); xref thì dùng `manage_xref`.
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
| `XrefTools.cs` | xref |
| `ViewTools.cs` | zoom, screenshot, named view |
| `CommandTools.cs` | `run_command`, `cancel_command`, `reload_addin` |

Agent Cursors (`src/T3LabCAD.Core/`): `AgentCursors.cs` (theo dõi bước, tên AI, `t3lab_narrate`, cài đặt), `ActivityPalette.cs` (bảng hoạt động), `CursorOverlay.cs` (con trỏ + khung trên bản vẽ bằng TransientManager). Hook trong `Rpc.CallTool`.

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
- `smoke_live.py` cần AutoCAD đang chạy (Windows). Chỉ **đọc** trên bản vẽ bạn đang mở. Mọi thao tác ghi làm trên một bản vẽ mới: tạo / đổi tên / xoá layout, style, linetype, block, group, view; explode; purge; đo (diện tích hình chữ nhật 1000 × 500 = 500 000, offset 100 ra ngoài = 840 000), join, boundary, giao điểm; table, mleader, 5 kiểu dim + dim_chain; `replace_text` (tiếng Việt, mọi kiểu chữ hoa / thường); block có attribute + `sync_block_attributes`; `set_viewport`; và kiểm tra 1 lần gọi = 1 bước Undo (`_.U`). Import layer từ file khác và CSV cũng chạy trong thư mục tạm. Phần file (lưu, bản sao 2013, xref, đổi tên, xoá, export / publish PDF) làm trong thư mục tạm `%TEMP%\t3labcad_live_<pid>`, xoá khi xong. Cuối cùng đóng bản vẽ thử không lưu và kiểm tra `reload_addin`.

Log: `%APPDATA%\T3LabCAD\logs\t3labcad.log`.

## Phát hành

`.github/workflows/release.yml` build 3 series trên Ubuntu, chạy `python scripts/package.py` và đưa `T3LabCAD.zip` lên GitHub Release `v<Version>` (`src/Directory.Build.props`). Chạy mỗi khi merge vào `main`, khi push tag `v*`, hoặc bấm **Run workflow** (Actions › Release). Cùng version thì file zip được thay; đổi `<Version>` để ra release mới (cả `src/Directory.Build.props`, `AppVersion` trong `bundle/PackageContents.xml` và `Entry.Version`). MCP Control trong T3Lab tải bản mới nhất từ `releases/latest/download/T3LabCAD.zip`.

## Lịch sử phiên bản

| Version | Nội dung |
|---|---|
| 1.3.0 | 47 → 55 tool. `measure`, `quantity_takeoff`, `extract_block_data` (CSV), `replace_text`, `edit_geometry` (offset, join, boundary), `sync_block_attributes`, `import_from_drawing`, `set_viewport`. `add_entities` thêm table, mleader, attdef, dim radius / diameter / angular / arc / ordinate / chain. `update_entities` thêm dynamic block property, đỉnh polyline, ô table, draw order. `query_entities` thêm length / area, lọc màu / linetype / closed, `totals`. |
| 1.2.0 | 27 → 47 tool. File (`list_drawing_files`, `rename_document`, `delete_drawing_file`; `save_document` bản sao / bản cũ; `new_document save_as`), layout + viewport, text / dim style, linetype, `list_named_objects` / `rename_object` / `delete_object` / `purge`, group, named view, xref, `explode_entities`, `export_pdf`, `publish_pdf`. `scripts/gen_tools_snapshot.py` + kiểm tra trong CI. |
| 1.1.2 | Icon ribbon T3Lab không bị cắt; nút play / stop đặc. |
| 1.1.1 | Icon ribbon theo giao diện sáng / tối của AutoCAD. |
| 1.1.0 | Hỗ trợ AutoCAD / Civil 3D 2022–2027 (R24, R25, R26); file `T3LabCAD.zip` cho MCP Control; release mỗi khi merge vào `main`. |
| 1.0.0 | Add-in AutoCAD + bridge stdio, 27 tool. |

## Dự kiến (chưa làm)

Đợt 1 (10 bước) đã xong trong 1.3.0. Tiếp theo:

1. `edit_geometry`: extend, trim, break, fillet / chamfer (line-line trước).
2. `add_entities` / `query_entities` / `delete_entities` với `space: "block:TÊN"` để sửa định nghĩa block không cần BEDIT.
3. `transform_entities`: array (lưới, tròn), align.
4. Field trong text / mtext (tên file, ngày, diện tích đối tượng).
5. `set_page_setup`, `layer_state`, `attach_underlay` (ảnh, PDF, DWF, DGN), `export_entities` (WBLOCK), `set_ucs`, point cloud cho Scan-to-BIM, và tool Civil 3D (alignment, surface, COGO point) trong một DLL riêng chỉ nạp khi chạy Civil 3D.
