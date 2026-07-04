# Hatch & Wipeout AutoCAD Tool

Tiện ích AutoCAD hỗ trợ tạo Hatch Solid và Wipeout tự động bên trong các Block Reference được chọn.

## Các tính năng chính

1. **Lệnh TH (Block Hatch)**
   * Chọn các đối tượng Block Reference trong bản vẽ.
   * Tự động tạo Hatch Solid theo hình bao lồi (Convex Hull) trực tiếp bên trong định nghĩa của Block (Block Definition).

2. **Lệnh TW (Block Wipeout)**
   * Chọn các đối tượng Block Reference trong bản vẽ.
   * Tự động tính toán ranh giới hình học từ các đường cong (Curves) bên trong Block (bao gồm cả phân tích Region từ các đường khép kín và hở) để tạo đối tượng Wipeout che nền trực tiếp bên trong định nghĩa của Block (Block Definition).

## Giao diện Ribbon

Tiện ích tự động tích hợp một bảng điều khiển trên thanh Ribbon sau khi được load thành công vào AutoCAD:
* **Nút Block Hatch**: Gọi lệnh `TH` để tạo Hatch Solid.
* **Nút Block Wipeout**: Gọi lệnh `TW` để tạo Wipeout che nền.
