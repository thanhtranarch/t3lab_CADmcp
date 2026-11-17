# T3Lab CAD MCP — add-in AutoCAD và 3ds Max cho Claude

Hai add-in, một bridge: **T3LabCAD** (AutoCAD / Civil 3D 2022–2027) và **T3LabMax** (3ds Max 2022–2027, xem [mục 3ds Max](#3ds-max--t3labmax)).

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

## 3ds Max — T3LabMax

```
Claude ──stdio MCP──> bridge.py (%APPDATA%\T3LabMax) ──TCP 127.0.0.1:48910-48918──> t3lab_max_mcp.py trong 3ds Max
```

| Phần | Vai trò |
|---|---|
| `src/T3LabMax/t3lab_max_mcp.py` | Server TCP (chỉ localhost) viết bằng Python + `pymxs`, chạy được từ 3ds Max 2022 (Python 3.7) tới 2027. Socket thread chỉ trả `health` / `tools/list`; `tools/call` xếp hàng và chạy trên main thread qua một Qt timer 25 ms (PySide2 cho 2022–2024, PySide6 cho 2025+). Mỗi tool ghi = 1 bước Undo `T3Lab MCP: <tool>`. |
| `src/T3LabMax/t3lab_max_mcp_start.ms` | Script khởi động: thêm thư mục vào `sys.path`, chạy server. MAXScript Listener: `T3MCPStatus()`, `T3MCPStart()`, `T3MCPStop()`. |
| `bundle_max/PackageContents.xml` | Package 3ds Max (`post-start-up scripts parts`, `SeriesMin 2022` – `SeriesMax 2027`). Không có phần compiled nên một bundle cho mọi bản. |
| `bridge/t3lab_cad_bridge.py` | Cùng bridge với AutoCAD: deploy vào `%APPDATA%\T3LabMax\bridge.py` thì tự chọn profile 3ds Max (server `t3lab-3dsmax`, pin `max_port`). Env `T3LAB_BRIDGE_APP=T3LabMax` để ép profile. |

**Cài:** T3Lab › MCP Control › Connect › 3ds Max › **Connect** (tải `T3LabMax.zip`, cài bundle vào `%APPDATA%\Autodesk\ApplicationPlugins\T3LabMax.bundle` và bridge vào `%APPDATA%\T3LabMax`). Khởi động lại 3ds Max sau lần cài đầu. Cài tay: giải nén zip, `T3LabMax.bundle` → `%APPDATA%\Autodesk\ApplicationPlugins`, `T3LabMax` → `%APPDATA%`.

| Nhóm | Tool |
|---|---|
| Trạng thái | `get_status` |
| Đọc | `list_objects` (lọc theo tên, class, layer, selection; `detail=count/bbox`; phân trang), `get_object`, `list_layers`, `list_materials` |
| Chọn / xem | `select_objects`, `zoom_extents`, `viewport_screenshot` (ảnh PNG) |
| Tạo / sửa | `create_objects` (Box, Sphere, Cylinder, Plane, Text, …), `transform_objects`, `set_properties`, `assign_material` (Physical / Standard), `delete_objects`, `undo_last` |
| MAXScript | `run_maxscript` — từ chối DOSCommand, ShellLaunch, deleteFile, python.Execute, saveMaxFile, resetMaxFile, quitMax… |
| Bridge | `list_3dsmax_instances`, `switch_3dsmax_instance` |

Test (không cần 3ds Max, dùng pymxs giả): `python -I tests/test_max_server.py`. **Chưa test trong 3ds Max thật.**

## Phát hành

`.github/workflows/release.yml` build 3 series trên Ubuntu, chạy `python scripts/package.py` (ra `T3LabCAD.zip` và `T3LabMax.zip`) và đưa cả hai lên GitHub Release `v<Version>` (`src/Directory.Build.props`). Chạy khi push tag `v*` hoặc bấm **Run workflow** (Actions › Release). MCP Control trong T3Lab tải bản mới nhất từ `releases/latest/download/T3LabCAD.zip` / `T3LabMax.zip`.
