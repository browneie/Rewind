// Draws Rewind's app icon and writes Resources/AppIcon.icns.
// Run from the repo root: swift scripts/make-icon.swift
import AppKit
import CoreGraphics

let size = 1024
let ctx = CGContext(data: nil, width: size, height: size, bitsPerComponent: 8, bytesPerRow: 0,
                    space: CGColorSpace(name: CGColorSpace.sRGB)!,
                    bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
// Work in a y-down, 0–100 design space mapped onto the macOS icon grid (824 px shape, 100 px margin).
ctx.translateBy(x: 0, y: CGFloat(size))
ctx.scaleBy(x: 1, y: -1)
let k: CGFloat = 824 / 92, o: CGFloat = 100 - 4 * k
func P(_ x: CGFloat, _ y: CGFloat) -> CGPoint { CGPoint(x: o + x * k, y: o + y * k) }
func L(_ v: CGFloat) -> CGFloat { v * k }
func rgb(_ hex: UInt32, _ a: CGFloat = 1) -> CGColor {
    CGColor(srgbRed: CGFloat((hex >> 16) & 0xFF) / 255, green: CGFloat((hex >> 8) & 0xFF) / 255,
            blue: CGFloat(hex & 0xFF) / 255, alpha: a)
}

// Squircle body with a soft drop shadow.
let body = CGRect(origin: P(4, 4), size: CGSize(width: L(92), height: L(92)))
let shape = CGPath(roundedRect: body, cornerWidth: L(22), cornerHeight: L(22), transform: nil)
ctx.saveGState()
ctx.setShadow(offset: CGSize(width: 0, height: -10), blur: 24, color: rgb(0x000000, 0.35))
ctx.addPath(shape); ctx.setFillColor(rgb(0x0c0c10)); ctx.fillPath()
ctx.restoreGState()

ctx.saveGState()
ctx.addPath(shape); ctx.clip()
let grad = CGGradient(colorsSpace: CGColorSpace(name: CGColorSpace.sRGB)!,
                      colors: [rgb(0x2a2a33), rgb(0x0c0c10)] as CFArray, locations: [0, 1])!
ctx.drawLinearGradient(grad, start: P(50, 4), end: P(50, 96), options: [])
// faint top sheen
let sheen = CGGradient(colorsSpace: CGColorSpace(name: CGColorSpace.sRGB)!,
                       colors: [rgb(0xffffff, 0.08), rgb(0xffffff, 0)] as CFArray, locations: [0, 1])!
ctx.drawLinearGradient(sheen, start: P(50, 4), end: P(50, 45), options: [])
ctx.restoreGState()

// Hairline border.
ctx.addPath(CGPath(roundedRect: body.insetBy(dx: L(0.5), dy: L(0.5)), cornerWidth: L(21.5), cornerHeight: L(21.5), transform: nil))
ctx.setStrokeColor(rgb(0xffffff, 0.12)); ctx.setLineWidth(L(1)); ctx.strokePath()

// White rewind ring: a 309° arc, counterclockwise from upper-left round to the top.
let white = rgb(0xffffff)
let ring = CGMutablePath()
let start = atan2(34.0 - 50, 30.5 - 50), sweep = 309.4 * Double.pi / 180
for i in 0...240 {
    let a = start - sweep * Double(i) / 240
    let pt = P(50 + 25 * CGFloat(cos(a)), 50 + 25 * CGFloat(sin(a)))
    i == 0 ? ring.move(to: pt) : ring.addLine(to: pt)
}
ctx.addPath(ring)
ctx.setStrokeColor(white); ctx.setLineWidth(L(6)); ctx.setLineCap(.round); ctx.setLineJoin(.round)
ctx.strokePath()

// Arrowhead.
let head = CGMutablePath()
head.move(to: P(42, 25)); head.addLine(to: P(52.5, 17.5)); head.addLine(to: P(52.5, 32.5)); head.closeSubpath()
ctx.addPath(head); ctx.setFillColor(white); ctx.fillPath()
ctx.addPath(head); ctx.setLineWidth(L(3)); ctx.strokePath()

// Red record dot.
ctx.setFillColor(rgb(0xff4d5e))
ctx.fillEllipse(in: CGRect(origin: P(38, 38), size: CGSize(width: L(24), height: L(24))))

// Write 1024 PNG, then the iconset and .icns.
let image = ctx.makeImage()!
let fm = FileManager.default
let set = URL(fileURLWithPath: "build/AppIcon.iconset")
try? fm.removeItem(at: set)
try! fm.createDirectory(at: set, withIntermediateDirectories: true)
func write(_ px: Int, _ name: String) {
    let rep = NSBitmapImageRep(cgImage: image)
    let scaled = NSImage(size: NSSize(width: px, height: px))
    scaled.addRepresentation(rep)
    let out = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: px, pixelsHigh: px, bitsPerSample: 8,
                               samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB,
                               bytesPerRow: 0, bitsPerPixel: 0)!
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: out)
    NSGraphicsContext.current?.imageInterpolation = .high
    rep.draw(in: NSRect(x: 0, y: 0, width: px, height: px))
    NSGraphicsContext.restoreGraphicsState()
    try! out.representation(using: .png, properties: [:])!.write(to: set.appendingPathComponent(name))
}
for (px, name) in [(16, "icon_16x16"), (32, "icon_16x16@2x"), (32, "icon_32x32"), (64, "icon_32x32@2x"),
                   (128, "icon_128x128"), (256, "icon_128x128@2x"), (256, "icon_256x256"), (512, "icon_256x256@2x"),
                   (512, "icon_512x512"), (1024, "icon_512x512@2x")] {
    write(px, name + ".png")
}
try! NSBitmapImageRep(cgImage: image).representation(using: .png, properties: [:])!
    .write(to: URL(fileURLWithPath: "docs/icon.png"))
print("wrote build/AppIcon.iconset and docs/icon.png")
