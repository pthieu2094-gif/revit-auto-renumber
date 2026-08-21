# Sheet Numbering Revit Add-in

Plugin Revit 2025 để tự động đánh số Sheet Number cho các bản vẽ (ViewSheet).

## Tính năng

- Đánh số lại toàn bộ hoặc một nhóm sheet trong project Revit
- Sheet Number được tạo tuần tự theo thứ tự người dùng chọn
- Không thay đổi tên sheet (Sheet Name), chỉ thay đổi Sheet Number
- Hỗ trợ Prefix (tiền tố): A-, AR-, SK-, etc.
- Hỗ trợ Number Padding: 0, 1, 2, 3, 4 chữ số
- Kiểm tra trùng số trước khi áp dụng
- Rollback toàn bộ nếu có lỗi
- Giao diện tiếng Việt, WPF hiện đại
- Logging ra file text

## Yêu cầu hệ thống

- Revit 2025
- .NET 8 SDK
- Visual Studio 2022 (khuyến nghị)
- Revit API 2025 DLLs

## Cấu trúc dự án

```
Renumber Sheets/
├── SheetNumberingRevit.csproj       # Project file (.NET 8, Revit 2025 API)
├── SheetNumberingRevit.addin        # Revit Add-in manifest
├── SheetNumberingRevit_Ribbon.addin # Alternative ribbon manifest
├── Directory.Build.props             # Build configuration
├── BuildAndInstall.ps1              # Script build & cài đặt tự động
├── README.md                       # Documentation
│
├── Commands/                        # External Commands
│   └── RenumberSheetsCommand.cs    # IExternalCommand implementation
│
├── Models/                         # Data Models
│   ├── SheetInfo.cs                # Sheet data model
│   ├── SortType.cs                 # Sort type enum
│   ├── NumberingOptions.cs         # Numbering configuration
│   └── NumberingResult.cs          # Operation result
│
├── ViewModels/                     # MVVM ViewModels
│   └── MainViewModel.cs            # Main ViewModel (Design-time + Runtime)
│
├── Views/                          # WPF Views
│   ├── MainWindow.xaml             # Main UI (hỗ trợ Design-time data)
│   └── MainWindow.xaml.cs          # Code-behind
│
├── Services/                       # Business Logic Services
│   ├── SheetNumberingService.cs    # Business logic service
│   └── LoggingService.cs           # Logging service
│
└── Helpers/                        # Helper Classes
    ├── ViewModelBase.cs            # MVVM base class
    ├── RelayCommand.cs             # ICommand implementations
    └── Converters.cs               # WPF value converters
```

## Xem giao diện trong WPF Designer (Visual Studio)

Dự án hỗ trợ **Design-time data** để bạn có thể xem giao diện WPF mà không cần chạy Revit.

### Cách mở WPF Designer

1. Mở file `Views/MainWindow.xaml` trong Visual Studio
2. Nhấn **Shift + F7** để mở Designer
3. Giao diện sẽ hiển thị với dữ liệu mẫu:

### Dữ liệu mẫu trong Designer

Khi mở Designer, bạn sẽ thấy 5 sheet mẫu:
| Sheet Number | Sheet Name | Selected |
|-------------|------------|----------|
| A-01 | Mặt bằng tầng 1 | ✓ |
| A-02 | Mặt bằng tầng 2 | ✓ |
| A-03 | Mặt đứng | ✓ |
| B-01 | Mặt cắt A-A | ✗ |
| B-02 | Chi tiết cột | ✗ |

### Tính năng Design-time

- **Không gọi Revit API** khi đang trong Designer
- ViewModel tự động tạo dữ liệu mẫu khi `DesignerProperties.GetIsInDesignMode` trả về `true`
- Các nút "Áp dụng" và "Xem trước" bị vô hiệu hóa trong Design Mode

### Lưu ý

- Khi chạy thực từ Revit, ViewModel sẽ nhận `UIDocument` và lấy dữ liệu sheet thật
- Luồng chạy của Revit Add-in không bị ảnh hưởng

## Hướng dẫn Build

### Bước 1: Cài đặt Revit API References

1. Tìm thư mục cài đặt Revit 2025, thường nằm tại:
   ```
   C:\Program Files\Autodesk\Revit 2025\
   ```

2. Trong file `SheetNumberingRevit.csproj`, cập nhật đường dẫn `REVIT_API_PATH`:
   ```xml
   <PropertyGroup>
     <REVIT_API_PATH>C:\Program Files\Autodesk\Revit 2025\</REVIT_API_PATH>
   </PropertyGroup>
   ```

   Hoặc đặt biến môi trường `REVIT_API_PATH` trỏ đến thư mục chứa Revit API DLLs.

### Bước 2: Build Project

1. Mở `SheetNumberingRevit.csproj` trong Visual Studio 2022
2. Chọn Configuration: Release, Platform: x64
3. Build Solution (Ctrl+Shift+B)

### Bước 3: Copy DLL

Sau khi build thành công, copy file output:
```
bin\x64\Release\net8.0-windows\SheetNumberingRevit.dll
```

đến thư mục bạn muốn lưu trữ plugin, ví dụ:
```
C:\RevitAddins\SheetNumbering\
```

### Bước 4: Cập nhật file .addin

Mở file `SheetNumberingRevit.addin` và cập nhật đường dẫn Assembly:

```xml
<Assembly>C:\RevitAddins\SheetNumbering\SheetNumberingRevit.dll</Assembly>
```

### Bước 5: Cài đặt Add-in vào Revit

1. Copy file `SheetNumberingRevit.addin` đã chỉnh sửa đến:
   ```
   %AppData%\Autodesk\Revit\Addins\2025\
   ```

   Đường dẫn đầy đủ:
   ```
   C:\Users\[Username]\AppData\Roaming\Autodesk\Revit\Addins\2025\
   ```

2. Khởi động Revit 2025

3. Plugin sẽ xuất hiện trong Ribbon Tab "TOOLS API" > Panel "Sheet Numbering"

## Hướng dẫn Debug

### Phương pháp 1: Attach Process

1. Trong Visual Studio, mở Solution chứa project
2. Build project ở chế độ Debug
3. Set breakpoints tại các vị trí cần debug
4. Trong Revit, mở project và chạy lệnh plugin
5. Trong Visual Studio: Debug > Attach to Process
6. Chọn process `Revit.exe`
7. Click Attach

### Phương pháp 2: Debug Configuration

Thêm đoạn sau vào file `.csproj` để enable debugging:

```xml
<PropertyGroup Condition="'$(Configuration)' == 'Debug'">
  <DebugSymbols>true</DebugSymbols>
  <DebugType>full</DebugType>
  <Optimize>false</Optimize>
</PropertyGroup>
```

## Cách sử dụng

### Giao diện chính

1. **Tùy chọn đánh số:**
   - **Prefix (Tiền tố):** Nhập tiền tố cho sheet number, ví dụ: `A-`, `AR-`, `SK-`
   - **Start Number (Số bắt đầu):** Số bắt đầu đánh số, mặc định là 1
   - **Number Padding (Số chữ số):** Số lượng chữ số tối thiểu:
     - 0 → A-1, A-2
     - 2 → A-01, A-02
     - 3 → A-001, A-002

2. **Danh sách Sheet:**
   - Tick chọn các sheet cần đánh số
   - Xem Sheet Number hiện tại và Sheet Name
   - Xem Sheet Number mới sau khi xem trước

3. **Sắp xếp:**
   - Theo Sheet Number hiện tại
   - Theo Sheet Name
   - Theo thứ tự chọn thủ công (dùng nút Lên/Xuống)

4. **Nút điều khiển:**
   - **Xem trước:** Xem kết quả trước khi áp dụng
   - **Áp dụng:** Thực hiện đánh số
   - **Hủy:** Đóng cửa sổ mà không thay đổi

### Quy tắc đánh số

- Công thức: `{Prefix}{Số thứ tự đã padding}`
- Ví dụ: Prefix = "A-", Start = 1, Padding = 2:
  - A-01, A-02, A-03, ...

### Kiểm tra trùng số

Trước khi áp dụng, plugin kiểm tra:
1. Trùng trong danh sách chọn
2. Trùng với sheet không được chọn (nếu checkbox "Giữ nguyên sheet không được chọn" được tick)

Nếu phát hiện trùng, plugin sẽ hiển thị thông báo lỗi và không thực hiện thay đổi.

### Rollback

Nếu có lỗi xảy ra trong quá trình đánh số, toàn bộ thay đổi sẽ được rollback để đảm bảo project không bị thay đổi dở dang.

## Logging

File log được lưu tại:
```
%AppData%\SheetNumberingRevit\logs\log_[YYYYMMDD].txt
```

## Giải quyết sự cố

### Lỗi "Could not load file or assembly"

Kiểm tra:
1. Đường dẫn Assembly trong file .addin đúng
2. Revit API DLLs tồn tại tại đường dẫn đã chỉ định

### Plugin không xuất hiện trong Ribbon

1. Đảm bảo file .addin nằm đúng thư mục
2. Kiểm tra file .addin có syntax đúng không
3. Restart Revit

### Lỗi khi đánh số

Kiểm tra file log để xem chi tiết lỗi.

## License

MIT License - Sử dụng tự do cho mục đích cá nhân và thương mại.

## Tác giả

TOOLS API

## Phiên bản

- **1.0.0** - Phiên bản đầu tiên hỗ trợ Revit 2025
