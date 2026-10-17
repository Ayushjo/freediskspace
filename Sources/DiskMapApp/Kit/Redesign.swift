import DiskMapCore
import SwiftUI

// The 2026 redesign (dist/ux-boards/app2): white pages, a paper sidebar,
// hairline cards, one violet, and violet hatching for "can be freed".

extension DiskMapTheme {
    /// The page behind cards (the window's main area).
    static let page = adaptive(light: (255, 255, 255), dark: (14, 14, 16))
    /// The sidebar and secondary columns (receipt column, inspector rail).
    static let sidebar = adaptive(light: (252, 252, 251), dark: (11, 11, 12))
    /// A card on the page.
    static let card = adaptive(light: (255, 255, 255), dark: (20, 20, 23))
    /// Selected nav row, filled rows, quiet wells.
    static let soft = adaptive(light: (244, 244, 241), dark: (30, 30, 34))
    /// The dark "You can free" panel. Stays dark in both appearances.
    static let night = adaptive(light: (16, 17, 19), dark: (28, 28, 33))
    /// Violet on the dark panel and in dark mode figures.
    static let accentBright = adaptive(light: (155, 138, 240), dark: (170, 156, 245))
    /// Text on accent fills.
    static let onAccent = Color.white
    /// The deep violet used for "rebuilds" text on light hatching.
    static let accentDeep = adaptive(light: (91, 72, 184), dark: (180, 168, 248))
    /// The pale violet behind hatching and selected table rows.
    static let accentWash = adaptive(light: (243, 240, 253), dark: (36, 32, 58))
}

extension DiskMapType {
    /// The hero figure on Overview ("148 GB"); sized by the caller's width class.
    static func hero(_ size: CGFloat) -> Font { .system(size: size * scale, weight: .semibold).monospacedDigit() }
    /// Page titles ("Choose what to free").
    static var pageTitle: Font { .system(size: 26 * scale, weight: .semibold) }
    /// Card titles ("Biggest places").
    static var cardTitle: Font { .system(size: 14 * scale, weight: .semibold) }
}

/// Window width classes the boards were drawn for: 13" Air, 14" Pro, a large display.
enum WidthClass: Equatable {
    case compact, regular, wide
    init(_ width: CGFloat) {
        self = width < 1_400 ? .compact : width < 1_700 ? .regular : .wide
    }
    var pagePadding: CGFloat { self == .compact ? 18 : self == .regular ? 24 : 28 }
    var gap: CGFloat { self == .compact ? 12 : self == .regular ? 16 : 18 }
}

private struct WidthClassKey: EnvironmentKey { static let defaultValue: WidthClass = .regular }
extension EnvironmentValues {
    /// Set by the shell from the window width.
    var widthClass: WidthClass {
        get { self[WidthClassKey.self] }
        set { self[WidthClassKey.self] = newValue }
    }
}

/// Violet diagonal stripes: the one mark for "can be freed / rebuilds by itself".
struct Hatch: View {
    var stripe: Color = DiskMapTheme.accent
    var ground: Color = DiskMapTheme.accentWash
    var spacing: CGFloat = 7
    var width: CGFloat = 3
    var body: some View {
        Canvas { ctx, size in
            ctx.fill(Path(CGRect(origin: .zero, size: size)), with: .color(ground))
            var path = Path()
            var x = -size.height
            while x < size.width + size.height {
                path.move(to: CGPoint(x: x, y: size.height))
                path.addLine(to: CGPoint(x: x + size.height, y: 0))
                x += spacing
            }
            ctx.stroke(path, with: .color(stripe), lineWidth: width)
        }
        .accessibilityHidden(true)
    }
}

/// Faint vertical stripes for free space in a bar.
struct FreeStripes: View {
    var body: some View {
        Canvas { ctx, size in
            ctx.fill(Path(CGRect(origin: .zero, size: size)), with: .color(DiskMapTheme.card))
            var x: CGFloat = 0
            while x < size.width {
                ctx.fill(Path(CGRect(x: x, y: 0, width: 2, height: size.height)), with: .color(DiskMapTheme.line.opacity(0.9)))
                x += 6
            }
        }
        .overlay(Rectangle().stroke(DiskMapTheme.line, lineWidth: 1))
        .accessibilityHidden(true)
    }
}

/// The 24 pt grid some boards show faintly behind a surface.
struct BoardGrid: View {
    var tint: Color = DiskMapTheme.ink.opacity(0.045)
    var step: CGFloat = 24
    var body: some View {
        Canvas { ctx, size in
            var p = Path()
            var x: CGFloat = 0
            while x <= size.width { p.move(to: CGPoint(x: x, y: 0)); p.addLine(to: CGPoint(x: x, y: size.height)); x += step }
            var y: CGFloat = 0
            while y <= size.height { p.move(to: CGPoint(x: 0, y: y)); p.addLine(to: CGPoint(x: size.width, y: y)); y += step }
            ctx.stroke(p, with: .color(tint), lineWidth: 1)
        }
        .allowsHitTesting(false)
        .accessibilityHidden(true)
    }
}

/// A hairline card on the page.
struct BoardCard<Content: View>: View {
    var padding: CGFloat? = nil
    @ViewBuilder var content: Content
    var body: some View {
        content
            .padding(padding ?? 0)
            .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(DiskMapTheme.card))
            .overlay(RoundedRectangle(cornerRadius: 12, style: .continuous).stroke(DiskMapTheme.line, lineWidth: 1))
            .clipShape(RoundedRectangle(cornerRadius: 12, style: .continuous))
    }
}

/// A card header: title, optional detail, trailing content.
struct BoardCardHeader<Trailing: View>: View {
    var title: String
    @ViewBuilder var trailing: Trailing
    var body: some View {
        HStack(spacing: 10) {
            Text(title).font(DiskMapType.cardTitle).foregroundStyle(DiskMapTheme.ink)
            Spacer(minLength: 8)
            trailing
        }
        .padding(.horizontal, 16)
        .frame(height: 44)
        .overlay(alignment: .bottom) { Rectangle().fill(DiskMapTheme.line).frame(height: 1) }
    }
}

extension BoardCardHeader where Trailing == EmptyView {
    init(title: String) { self.title = title; self.trailing = EmptyView() }
}

/// Filled violet: the one "go" action on a screen.
struct AccentButtonStyle: ButtonStyle {
    var large = false
    var fullWidth = false
    /// The boards' hard ink offset shadow, for the single biggest action.
    var hard = false
    @Environment(\.isEnabled) private var isEnabled
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(large ? .system(size: DiskMapType.scaled(14), weight: .semibold) : DiskMapType.bodyEmphasis)
            .lineLimit(1)
            .frame(maxWidth: fullWidth ? .infinity : nil)
            .padding(.horizontal, large ? 18 : 12)
            .frame(height: large ? 40 : DiskMapMetric.controlHeight)
            .foregroundStyle(DiskMapTheme.onAccent)
            .background(
                RoundedRectangle(cornerRadius: 8, style: .continuous)
                    .fill(DiskMapTheme.accent.opacity(!isEnabled ? 0.4 : configuration.isPressed ? 0.85 : 1))
                    .overlay(hard ? RoundedRectangle(cornerRadius: 8, style: .continuous).stroke(DiskMapTheme.ink, lineWidth: 1) : nil)
                    .background(hard ? RoundedRectangle(cornerRadius: 8, style: .continuous).fill(DiskMapTheme.ink).offset(x: 3, y: 3) : nil)
            )
            .offset(x: configuration.isPressed && hard ? 2 : 0, y: configuration.isPressed && hard ? 2 : 0)
            .contentShape(Rectangle())
    }
}

/// A small mono tag ("REBUILDS", "SPOTIFY IS OPEN", "KEPT").
struct BoardTag: View {
    enum Tone { case neutral, accent, ink }
    var text: String
    var tone: Tone = .neutral
    var body: some View {
        Text(text.uppercased())
            .font(.system(size: DiskMapType.scaled(10), weight: .medium, design: .monospaced))
            .tracking(0.2)
            .lineLimit(1)
            .padding(.horizontal, 6)
            .frame(height: 18)
            .foregroundStyle(tone == .accent ? DiskMapTheme.accentDeep : tone == .ink ? DiskMapTheme.onInk : DiskMapTheme.ink2)
            .background(
                RoundedRectangle(cornerRadius: 5, style: .continuous)
                    .fill(tone == .accent ? DiskMapTheme.accentWash : tone == .ink ? DiskMapTheme.ink : Color.clear)
                    .overlay(RoundedRectangle(cornerRadius: 5, style: .continuous)
                        .stroke(tone == .accent ? DiskMapTheme.accent.opacity(0.35) : tone == .ink ? Color.clear : DiskMapTheme.line, lineWidth: 1))
            )
            .fixedSize()
    }
}

/// A one-line notice with a violet rule on its leading edge.
struct BoardBanner: View {
    var title: String
    var detail: String
    var actionTitle: String?
    var action: () -> Void = {}
    var onDismiss: (() -> Void)?
    var body: some View {
        HStack(spacing: 12) {
            Circle().fill(DiskMapTheme.accent).frame(width: 7, height: 7)
            (Text(title).font(DiskMapType.bodyEmphasis).foregroundColor(DiskMapTheme.ink)
             + Text("  " + detail).font(DiskMapType.body).foregroundColor(DiskMapTheme.ink2))
                .lineLimit(1)
                .truncationMode(.tail)
            Spacer(minLength: 8)
            if let actionTitle {
                Button(actionTitle, action: action).buttonStyle(SecondaryButtonStyle())
            }
            if let onDismiss {
                Button(action: onDismiss) { Image(systemName: "xmark") }
                    .buttonStyle(IconButtonStyle(size: 24))
                    .accessibilityLabel("Dismiss")
            }
        }
        .padding(.leading, 14)
        .padding(.trailing, 10)
        .frame(minHeight: 44)
        .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(DiskMapTheme.card))
        .overlay(RoundedRectangle(cornerRadius: 10, style: .continuous).stroke(DiskMapTheme.line, lineWidth: 1))
        .overlay(alignment: .leading) {
            UnevenRoundedRectangle(topLeadingRadius: 10, bottomLeadingRadius: 10)
                .fill(DiskMapTheme.accent).frame(width: 3)
        }
        .accessibilityElement(children: .contain)
    }
}

/// Square line icon tile used beside list items ("Ready to free").
struct IconTile: View {
    var systemName: String
    var size: CGFloat = 34
    var body: some View {
        Image(systemName: systemName)
            .font(.system(size: DiskMapType.scaled(14), weight: .regular))
            .foregroundStyle(DiskMapTheme.ink)
            .frame(width: size, height: size)
            .background(RoundedRectangle(cornerRadius: 9, style: .continuous).fill(DiskMapTheme.card))
            .overlay(RoundedRectangle(cornerRadius: 9, style: .continuous).stroke(DiskMapTheme.line, lineWidth: 1))
            .accessibilityHidden(true)
    }
}

/// Relative time for the page bar ("2 minutes ago").
enum RelativeTime {
    static func string(since date: Date, now: Date = Date()) -> String {
        let s = max(0, now.timeIntervalSince(date))
        if s < 45 { return "just now" }
        if s < 90 { return "a minute ago" }
        if s < 3_600 { return "\(Int((s / 60).rounded())) minutes ago" }
        if s < 5_400 { return "an hour ago" }
        if s < 86_400 { return "\(Int((s / 3_600).rounded())) hours ago" }
        let days = Int((s / 86_400).rounded())
        return days == 1 ? "yesterday" : "\(days) days ago"
    }
}
