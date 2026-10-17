import Foundation

/// Single shared size formatter for the whole UI.
enum ByteFormat {
    /// "0 bytes", never "Zero KB". Formatters are not free to build, and this
    /// runs for every row of every list.
    private static let formatter: ByteCountFormatter = {
        let formatter = ByteCountFormatter()
        formatter.countStyle = .file
        formatter.allowsNonnumericFormatting = false
        return formatter
    }()

    static func string(_ bytes: Int64) -> String {
        formatter.string(fromByteCount: bytes)
    }

    /// Three significant digits for big display figures: "45.5 GB", "120 GB", "8.41 GB".
    static func hero(_ bytes: Int64) -> String {
        let units = ["bytes", "KB", "MB", "GB", "TB", "PB"]
        var value = Double(max(0, bytes)); var unit = 0
        while value >= 1000, unit < units.count - 1 { value /= 1000; unit += 1 }
        if unit == 0 { return "\(Int(value)) bytes" }
        let digits = value >= 100 ? 0 : value >= 10 ? 1 : 2
        return String(format: "%.\(digits)f", value) + " " + units[unit]
    }
}

func diskByteString(_ bytes: Int64) -> String {
    ByteFormat.string(bytes)
}
