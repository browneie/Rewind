import AppKit
import SwiftUI

/// Rewind's own color picker: a hue/saturation wheel, a brightness slider, a hex field and the presets.
struct ColorWheelPicker: View {
    @Binding var hex: UInt32
    @State private var hue: Double = 0
    @State private var sat: Double = 0
    @State private var bri: Double = 1
    @State private var hexText = ""
    private let size: CGFloat = 190

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack(alignment: .center, spacing: 14) {
                wheel
                VStack(alignment: .leading, spacing: 10) {
                    RoundedRectangle(cornerRadius: 10, style: .continuous)
                        .fill(Color(hex: hex))
                        .frame(width: 72, height: 48)
                        .overlay(RoundedRectangle(cornerRadius: 10, style: .continuous).strokeBorder(.primary.opacity(0.15)))
                    Text("Hex").font(.caption).foregroundStyle(.secondary)
                    HStack(spacing: 2) {
                        Text("#").foregroundStyle(.secondary)
                        TextField("3DDC84", text: $hexText)
                            .textFieldStyle(.roundedBorder)
                            .font(.system(.body, design: .monospaced))
                            .frame(width: 80)
                            .onSubmit(applyHexText)
                    }
                }
            }
            VStack(alignment: .leading, spacing: 4) {
                Text("Brightness").font(.caption).foregroundStyle(.secondary)
                brightness
            }
            VStack(alignment: .leading, spacing: 6) {
                Text("Presets").font(.caption).foregroundStyle(.secondary)
                HStack(spacing: 8) {
                    ForEach(Theme.accentPresets, id: \.hex) { preset in
                        Button { set(hex: preset.hex) } label: {
                            Circle().fill(Color(hex: preset.hex)).frame(width: 20, height: 20)
                                .overlay(Circle().strokeBorder(.primary.opacity(hex == preset.hex ? 0.9 : 0.15),
                                                               lineWidth: hex == preset.hex ? 2 : 1))
                        }
                        .buttonStyle(.plain).help(preset.name)
                    }
                }
            }
        }
        .padding(16)
        .onAppear { set(hex: hex) }
    }

    // MARK: Wheel

    private var wheel: some View {
        let hues = (0...12).map { Color(hue: Double($0) / 12, saturation: 1, brightness: 1) }
        return ZStack {
            Circle().fill(AngularGradient(colors: hues, center: .center))
            Circle().fill(RadialGradient(colors: [.white, .white.opacity(0)], center: .center,
                                         startRadius: 0, endRadius: size / 2))
            Circle().fill(Color.black.opacity(1 - bri))
            Circle().strokeBorder(.primary.opacity(0.15))
            // Marker
            Circle()
                .fill(Color(hue: hue, saturation: sat, brightness: bri))
                .frame(width: 18, height: 18)
                .overlay(Circle().strokeBorder(.white, lineWidth: 2.5))
                .shadow(color: .black.opacity(0.4), radius: 2)
                .offset(x: cos(hue * 2 * .pi) * sat * size / 2, y: sin(hue * 2 * .pi) * sat * size / 2)
        }
        .frame(width: size, height: size)
        .contentShape(Circle())
        .gesture(DragGesture(minimumDistance: 0).onChanged { g in
            let dx = g.location.x - size / 2, dy = g.location.y - size / 2
            var angle = atan2(dy, dx) // clockwise from the right, like the gradient
            if angle < 0 { angle += 2 * .pi }
            hue = angle / (2 * .pi)
            sat = min(hypot(dx, dy) / (size / 2), 1)
            commit()
        })
    }

    private var brightness: some View {
        GeometryReader { geo in
            ZStack(alignment: .leading) {
                Capsule().fill(LinearGradient(colors: [.black, Color(hue: hue, saturation: sat, brightness: 1)],
                                              startPoint: .leading, endPoint: .trailing))
                    .overlay(Capsule().strokeBorder(.primary.opacity(0.15)))
                Circle().fill(.white).frame(width: 18, height: 18)
                    .shadow(color: .black.opacity(0.4), radius: 2)
                    .offset(x: bri * (geo.size.width - 18))
            }
            .contentShape(Rectangle())
            .gesture(DragGesture(minimumDistance: 0).onChanged { g in
                bri = min(max((g.location.x - 9) / (geo.size.width - 18), 0), 1)
                commit()
            })
        }
        .frame(height: 18)
    }

    // MARK: Model

    private func commit() {
        let ns = NSColor(calibratedHue: hue, saturation: sat, brightness: bri, alpha: 1).usingColorSpace(.sRGB)!
        let r = UInt32((ns.redComponent * 255).rounded()), g = UInt32((ns.greenComponent * 255).rounded())
        let b = UInt32((ns.blueComponent * 255).rounded())
        hex = r << 16 | g << 8 | b
        hexText = String(format: "%06X", hex)
    }

    private func set(hex newHex: UInt32) {
        hex = newHex & 0xFFFFFF
        hexText = String(format: "%06X", hex)
        let ns = NSColor(srgbRed: CGFloat((hex >> 16) & 0xFF) / 255, green: CGFloat((hex >> 8) & 0xFF) / 255,
                         blue: CGFloat(hex & 0xFF) / 255, alpha: 1).usingColorSpace(.deviceRGB)!
        var h: CGFloat = 0, s: CGFloat = 0, b: CGFloat = 0, a: CGFloat = 0
        ns.getHue(&h, saturation: &s, brightness: &b, alpha: &a)
        // Keep the hue where it was for greys, so dragging brightness/saturation back doesn't jump.
        if s > 0.001 { hue = h }
        sat = s
        bri = b
    }

    private func applyHexText() {
        let cleaned = hexText.trimmingCharacters(in: .whitespaces).replacingOccurrences(of: "#", with: "")
        guard cleaned.count == 6, let v = UInt32(cleaned, radix: 16) else { hexText = String(format: "%06X", hex); return }
        set(hex: v)
    }
}

/// Settings row: a swatch with its hex that opens the color wheel in a popover.
struct AccentColorButton: View {
    @Binding var hex: UInt32
    @State private var open = false

    var body: some View {
        Button { open.toggle() } label: {
            HStack(spacing: 8) {
                RoundedRectangle(cornerRadius: 5, style: .continuous).fill(Color(hex: hex))
                    .frame(width: 34, height: 18)
                    .overlay(RoundedRectangle(cornerRadius: 5, style: .continuous).strokeBorder(.primary.opacity(0.2)))
                Text(String(format: "#%06X", hex)).font(.system(.body, design: .monospaced)).foregroundStyle(.secondary)
                Image(systemName: "chevron.down").font(.caption2.weight(.bold)).foregroundStyle(.secondary)
            }
        }
        .buttonStyle(.plain)
        .popover(isPresented: $open, arrowEdge: .bottom) { ColorWheelPicker(hex: $hex) }
    }
}
