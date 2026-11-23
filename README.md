# T3Lab CAD MCP — add-in AutoCAD cho Claude

Add-in AutoCAD (giống T3Lab Revit MCP) cho phép Claude Desktop / Claude Code đọc và chỉnh **bản vẽ đang mở trong AutoCAD**: truy vấn đối tượng, đọc text, layer, block, vẽ, sửa, di chuyển, zoom, chụp màn hình, chạy lệnh AutoCAD.

```
Claude ──stdio MCP──> bridge.py (%APPDATA%\T3LabCAD) ──TCP 127.0.0.1:48900-48908──> T3LabCAD add-in trong AutoCAD
```

| Phần | Vai trò |
|---|---|
| `src/T3LabCAD.Loader` | DLL AutoCAD load (bundle autoloader). Lệnh `T3MCPSTART / T3MCPSTOP / T3MCPSTATUS / T3MCPRELOAD`, tab ribbon **T3Lab › MCP Server**. Nạp Core từ bytes nên Core thay được khi AutoCAD đang chạy. |
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
`T3LabCAD MCP v1.2.0: listening on 127.0.0.1:48900 | 45 tools | ...`

Nếu AutoCAD đang mở lúc cài lần đầu: khởi động lại AutoCAD, hoặc `NETLOAD` file `T3LabCAD.Loader.dll` trong thư mục bundle.

## Tool

| Nhóm | Tool |
|---|---|
| Trạng thái | `get_status`, `get_sysvars`, `set_sysvar` |
| Bản vẽ | `open_document` (tên không có thư mục = cạnh bản vẽ đang mở), `new_document` (`save_as`), `activate_document`, `save_document` (dwg / xuất dxf, `copy=true` lưu bản sao, `version` 2018/2013/2010/2007/2004/2000), `close_document` |
| File | `list_drawing_files` (duyệt thư mục, đánh dấu file đang mở), `rename_document` (đổi tên file, bản vẽ đang mở được đóng → đổi tên → mở lại), `delete_drawing_file` (vào Recycle Bin, trừ khi `permanent=true`) |
| Layout | `list_layouts`, `activate_layout`, `create_layout` (mới hoặc copy từ layout khác), `add_viewport` (vùng model + tỉ lệ 1:100) |
| Layer | `list_layers`, `set_layer` (tạo/sửa/đổi tên/on/freeze/lock/current), `delete_layer` (có `merge_into`) |
| Style / linetype | `set_text_style` (font TrueType hoặc SHX, height, width factor, oblique), `set_dim_style` (tạo từ style gốc, đặt mọi biến DIM*, tự cập nhật dim), `load_linetype` (từ acadiso.lin, có wildcard) |
| Đối tượng có tên | `list_named_objects`, `rename_object`, `delete_object`, `purge` — cho layer, block, layout, text_style, dim_style, linetype, view, ucs, group, mleader_style, table_style (xref chỉ list) |
| Group / view / xref | `set_group` (tạo, thêm/bớt entity), `save_view`, `restore_view`, `attach_xref` (attach/overlay, đường dẫn tương đối), `manage_xref` (detach/reload/unload/bind/insert/repath) |
| Đọc | `query_entities` (lọc theo type, layer, block, text, vùng chọn, handle; phân trang), `get_texts` (TEXT/MTEXT/ATTRIB/MLEADER), `list_blocks`, `selection` |
| Vẽ / sửa | `add_entities` (line, polyline, rectangle, circle, arc, ellipse, point, text, mtext, insert + attribute, dim_aligned, dim_linear, hatch, spline), `update_entities`, `delete_entities`, `transform_entities` (move/copy/rotate/scale/mirror), `create_block`, `explode_entities` (attribute → text giữ giá trị) |
| Xem | `zoom`, `screenshot` (ảnh PNG cho Claude xem) |
| Lệnh | `run_command` (gửi dòng lệnh AutoCAD, chờ xong, báo đối tượng mới tạo, tự Esc nếu lệnh còn treo), `cancel_command`, `reload_addin` |
| Bridge | `list_autocad_instances`, `switch_autocad_instance` |

Tạo / đổi tên / xoá theo loại đối tượng:

| Loại | Tạo | Đổi tên | Xoá |
|---|---|---|---|
| File bản vẽ | `new_document save_as`, `save_document` | `rename_document` | `delete_drawing_file` |
| Layer | `set_layer` | `set_layer new_name` / `rename_object` | `delete_layer` / `delete_object` / `purge` |
| Block | `create_block`, `add_entities insert block_file` | `rename_object` | `delete_object` (`erase_references`) / `purge` |
| Layout | `create_layout` | `rename_object` | `delete_object` |
| Text / dim style | `set_text_style`, `set_dim_style` | `new_name` / `rename_object` | `delete_object` / `purge` |
| Linetype | `load_linetype` | `rename_object` | `delete_object` / `purge` |
| View | `save_view` | `rename_object` | `delete_object` |
| Group | `set_group` | `rename_object` | `delete_object` (ungroup) |
| Xref | `attach_xref` | `rename_object kind=block` | `manage_xref detach` |
| Entity | `add_entities` | — | `delete_entities` |

An toàn: server chỉ nghe `127.0.0.1`. `run_command` từ chối AutoLISP (`(`, `!`) và các lệnh nạp/chạy code hoặc mở chương trình khác (SHELL, NETLOAD, APPLOAD, SCRIPT, VBARUN...). Tool ghi bị từ chối khi người dùng đang ở giữa một lệnh. `close_document` không đóng bản vẽ chưa lưu trừ khi `save=true` hoặc `discard_changes=true`; `rename_document` cũng vậy (`save_changes=true`). `delete_drawing_file` chỉ xoá file AutoCAD (.dwg, .dxf, .dwt, .dws, .bak, .sv$) không đang mở, mặc định vào Recycle Bin. `delete_object` / `purge` không xoá đối tượng còn đang dùng, layer 0, style Standard hay đối tượng current; `purge dry_run=true` chỉ báo cáo.

## Phát triển

Thêm tool: tạo method trong `src/T3LabCAD.Core/Tools/*.cs` và `Registry.Add(...)` trong `Register()` (schema viết bằng helper `Sch`). Chọn `ToolKind`:
- `Read` — chỉ đọc, có transaction sẵn (`ctx.Tr`).
- `Write` — khoá document, transaction tự commit khi trả về / rollback khi lỗi = 1 bước Undo.
- `App` — mở/đóng/chuyển bản vẽ, chạy trong application context của AutoCAD (`ExecuteInApplicationContext`). Lưu ý: code chạy ở document context mà đổi bản vẽ active sẽ bị treo tới khi bản vẽ cũ được active lại.

`bridge/tools_snapshot.json` là danh sách tool gửi cho Claude khi AutoCAD chưa mở. Sau khi thêm/sửa tool, tạo lại từ mã nguồn (không cần AutoCAD; CI kiểm tra bằng `--check`):

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

`smoke_bridge.py` chạy không cần AutoCAD (dùng `tests/mock_autocad.py`). `smoke_live.py` cần AutoCAD đang chạy: chỉ đọc trên bản vẽ đang mở, mọi thao tác ghi làm trên một bản vẽ mới rồi đóng không lưu.

Log: `%APPDATA%\T3LabCAD\logs\t3labcad.log`.

## Phát hành

`.github/workflows/release.yml` build 3 series trên Ubuntu, chạy `python scripts/package.py` và đưa `T3LabCAD.zip` lên GitHub Release `v<Version>` (`src/Directory.Build.props`). Chạy mỗi khi merge vào `main`, khi push tag `v*`, hoặc bấm **Run workflow** (Actions › Release). Cùng version thì file zip được thay; đổi `<Version>` để ra release mới. MCP Control trong T3Lab tải bản mới nhất từ `releases/latest/download/T3LabCAD.zip`.
