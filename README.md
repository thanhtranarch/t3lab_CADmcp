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

Hỗ trợ AutoCAD 2021–2024 (.NET Framework 4.8). Build mặc định với API AutoCAD 2024.

## Cài đặt

Cần: AutoCAD 2021–2024, .NET SDK 8+ (để build), Python 3.

```bash
powershell -ExecutionPolicy Bypass -File scripts/install.ps1
```

Script sẽ:
1. Build 2 project (`-AcadDir` để chọn bản AutoCAD khác).
2. Copy bundle vào `%APPDATA%\Autodesk\ApplicationPlugins\T3LabCAD.bundle` (vị trí trusted, AutoCAD tự load khi khởi động).
3. Copy bridge vào `%APPDATA%\T3LabCAD\bridge.py`.
4. Thêm server `t3lab-autocad` vào `claude_desktop_config.json` (có backup). Thêm `-ClaudeCode` để đăng ký cho Claude Code CLI (`claude mcp add --scope user`).

Sau đó **khởi động lại Claude Desktop**. Trong AutoCAD gõ `T3MCPSTATUS` để kiểm tra:
`T3LabCAD MCP v1.0.0: listening on 127.0.0.1:48900 | 27 tools | ...`

Nếu AutoCAD đang mở lúc cài lần đầu: khởi động lại AutoCAD, hoặc `NETLOAD` file `T3LabCAD.Loader.dll` trong thư mục bundle.

## Tool

| Nhóm | Tool |
|---|---|
| Trạng thái | `get_status`, `get_sysvars`, `set_sysvar` |
| Bản vẽ | `open_document`, `new_document`, `activate_document`, `save_document` (dwg / xuất dxf), `close_document`, `list_layouts`, `activate_layout` |
| Layer | `list_layers`, `set_layer` (tạo/sửa/đổi tên/on/freeze/lock/current), `delete_layer` (có `merge_into`) |
| Đọc | `query_entities` (lọc theo type, layer, block, text, vùng chọn, handle; phân trang), `get_texts` (TEXT/MTEXT/ATTRIB/MLEADER), `list_blocks`, `selection` |
| Vẽ / sửa | `add_entities` (line, polyline, rectangle, circle, arc, ellipse, point, text, mtext, insert + attribute, dim_aligned, dim_linear, hatch, spline), `update_entities`, `delete_entities`, `transform_entities` (move/copy/rotate/scale/mirror), `create_block` |
| Xem | `zoom`, `screenshot` (ảnh PNG cho Claude xem) |
| Lệnh | `run_command` (gửi dòng lệnh AutoCAD, chờ xong, báo đối tượng mới tạo, tự Esc nếu lệnh còn treo), `cancel_command`, `reload_addin` |
| Bridge | `list_autocad_instances`, `switch_autocad_instance` |

An toàn: server chỉ nghe `127.0.0.1`. `run_command` từ chối AutoLISP (`(`, `!`) và các lệnh nạp/chạy code hoặc mở chương trình khác (SHELL, NETLOAD, APPLOAD, SCRIPT, VBARUN...). Tool ghi bị từ chối khi người dùng đang ở giữa một lệnh. `close_document` không đóng bản vẽ chưa lưu trừ khi `save=true` hoặc `discard_changes=true`.

## Phát triển

Thêm tool: tạo method trong `src/T3LabCAD.Core/Tools/*.cs` và `Registry.Add(...)` trong `Register()` (schema viết bằng helper `Sch`). Chọn `ToolKind`:
- `Read` — chỉ đọc, có transaction sẵn (`ctx.Tr`).
- `Write` — khoá document, transaction tự commit khi trả về / rollback khi lỗi = 1 bước Undo.
- `App` — mở/đóng/chuyển bản vẽ, chạy trong application context của AutoCAD (`ExecuteInApplicationContext`). Lưu ý: code chạy ở document context mà đổi bản vẽ active sẽ bị treo tới khi bản vẽ cũ được active lại.

`bridge/tools_snapshot.json` là danh sách tool gửi cho Claude khi AutoCAD chưa mở; cập nhật từ `%APPDATA%\T3LabCAD\tools_cache.json` sau khi thêm tool.

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
