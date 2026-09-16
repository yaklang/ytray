import SwiftUI

/// Shared sizing for the management window and its configuration sheets.
/// Tray controls keep their separate, space-constrained presentation.
enum ManagerMetrics {
    static let controlHeight: CGFloat = 32
    static let pagePadding: CGFloat = 16
    static let sectionSpacing: CGFloat = 12
    static let sidebarWidth: CGFloat = 208
}

struct ManagerButtonStyle: ButtonStyle {
    enum Emphasis { case primary, secondary, destructive }
    var emphasis: Emphasis = .secondary
    var iconOnly = false

    func makeBody(configuration: Configuration) -> some View {
        ManagerButtonBody(configuration: configuration, emphasis: emphasis, iconOnly: iconOnly)
    }
}

private struct ManagerButtonBody: View {
    let configuration: ButtonStyleConfiguration
    let emphasis: ManagerButtonStyle.Emphasis
    let iconOnly: Bool
    @Environment(\.isEnabled) private var isEnabled
    @State private var isHovered = false

    private var accent: Color { Color(red: 0.73, green: 0.35, blue: 0.10) }
    private var fill: Color {
        switch emphasis {
        case .primary: return accent.opacity(configuration.isPressed ? 0.8 : 1)
        case .secondary: return Color.primary.opacity(configuration.isPressed ? 0.12 : isHovered ? 0.08 : 0.045)
        case .destructive: return Color.red.opacity(configuration.isPressed ? 0.16 : isHovered ? 0.11 : 0.06)
        }
    }

    var body: some View {
        configuration.label
            .font(.system(size: 13, weight: .medium))
            .lineLimit(1)
            .fixedSize(horizontal: true, vertical: false)
            .foregroundStyle(emphasis == .primary ? Color.white : emphasis == .destructive ? .red : .primary)
            .padding(.horizontal, iconOnly ? 0 : 12)
            .frame(width: iconOnly ? ManagerMetrics.controlHeight : nil, height: ManagerMetrics.controlHeight)
            .background(fill, in: RoundedRectangle(cornerRadius: 7))
            .overlay(RoundedRectangle(cornerRadius: 7).stroke(Color.primary.opacity(0.10)))
            .contentShape(RoundedRectangle(cornerRadius: 7))
            .opacity(isEnabled ? 1 : 0.45)
            .onHover { isHovered = $0 }
    }
}

/// A native menu with a full-height label; resizing a system Picker's outer
/// frame alone leaves its visible bezel at the smaller AppKit control height.
struct ManagerPicker<Selection: Hashable, Content: View>: View {
    let title: String
    let selectedTitle: String
    @Binding var selection: Selection
    @ViewBuilder let content: () -> Content
    @Environment(\.isEnabled) private var isEnabled

    var body: some View {
        Menu {
            Picker(title, selection: $selection, content: content).labelsHidden()
        } label: {
            Text(selectedTitle).lineLimit(1).truncationMode(.middle)
        }
        .menuStyle(.borderlessButton)
        .menuIndicator(.hidden)
        .font(.system(size: 13))
        .foregroundStyle(.primary)
        .tint(.primary)
        .frame(minWidth: 0, maxWidth: .infinity, alignment: .leading)
        .padding(.leading, 10)
        .padding(.trailing, 28)
        .frame(height: ManagerMetrics.controlHeight)
        .background(Color(nsColor: .controlBackgroundColor), in: RoundedRectangle(cornerRadius: 7))
        .overlay(RoundedRectangle(cornerRadius: 7).stroke(Color.primary.opacity(0.16)))
        .overlay(alignment: .trailing) {
            Image(systemName: "chevron.up.chevron.down")
                .font(.system(size: 10, weight: .semibold))
                .foregroundStyle(.secondary)
                .padding(.trailing, 10)
                .allowsHitTesting(false)
        }
        .contentShape(RoundedRectangle(cornerRadius: 7))
        .fixedSize(horizontal: false, vertical: true)
        .opacity(isEnabled ? 1 : 0.45)
        .help(selectedTitle)
        .accessibilityLabel(title)
        .accessibilityValue(selectedTitle)
    }
}

struct ManagerTextFieldStyle: TextFieldStyle {
    func _body(configuration: TextField<Self._Label>) -> some View {
        configuration
            .textFieldStyle(.plain)
            .font(.system(size: 13))
            .padding(.horizontal, 9)
            .frame(height: ManagerMetrics.controlHeight)
            .background(Color(nsColor: .textBackgroundColor), in: RoundedRectangle(cornerRadius: 7))
            .overlay(RoundedRectangle(cornerRadius: 7).stroke(Color.primary.opacity(0.16)))
    }
}

struct ManagerToggleRow: View {
    let title: String
    var detail: String?
    @Binding var isOn: Bool

    var body: some View {
        HStack(spacing: 12) {
            VStack(alignment: .leading, spacing: 2) {
                Text(title).font(.system(size: 13))
                if let detail {
                    Text(detail).font(.system(size: 11)).foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            Toggle(title, isOn: $isOn)
                .labelsHidden()
                .toggleStyle(.switch)
                .controlSize(.regular)
                .fixedSize()
                .tint(Brand.orange)
        }
        .frame(maxWidth: .infinity, minHeight: ManagerMetrics.controlHeight, alignment: .leading)
        .padding(.vertical, 5)
    }
}
