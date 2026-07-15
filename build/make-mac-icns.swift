// Wraps full-bleed icon art (the Windows .ico squares) into a HIG-conformant macOS
// app icon: 1024 canvas, 824x824 rounded rect centered (100px gutter), corner radius
// 185.4, drop shadow black@50% blur 28 offset y-12 — numbers from Apple's Big Sur icon
// template (developer.apple.com/forums/thread/670578, design resources).
//
// Usage: swift build/make-mac-icns.swift <in.png> <out.icns>
// Needs: iconutil (ships with macOS). The source art is drawn slightly oversized and
// clipped to the squircle so its own baked-in corners can never leave notches.

import AppKit
import Foundation

let args = CommandLine.arguments
guard args.count == 3 else { fputs("usage: make-mac-icns.swift <in.png> <out.icns>\n", stderr); exit(2) }
guard let src = NSImage(contentsOfFile: args[1]) else { fputs("cannot read \(args[1])\n", stderr); exit(1) }

let canvas: CGFloat = 1024
let plate: CGFloat = 824          // HIG: icon shape size on the 1024 canvas
let radius: CGFloat = 185.4       // HIG: corner radius
let inset = (canvas - plate) / 2  // 100px gutter

let composite = NSImage(size: NSSize(width: canvas, height: canvas))
composite.lockFocus()
if let ctx = NSGraphicsContext.current?.cgContext {
    let rect = CGRect(x: inset, y: inset, width: plate, height: plate)
    let path = CGPath(roundedRect: rect, cornerWidth: radius, cornerHeight: radius, transform: nil)

    // Shadow pass: fill the plate shape once with the shadow enabled.
    ctx.saveGState()
    ctx.setShadow(offset: CGSize(width: 0, height: -12), blur: 28,
                  color: CGColor(gray: 0, alpha: 0.5))
    ctx.addPath(path)
    ctx.setFillColor(CGColor(gray: 1, alpha: 1))
    ctx.fillPath()
    ctx.restoreGState()

    // Art pass: clip to the squircle, draw the art a hair oversized (+6px each side).
    ctx.saveGState()
    ctx.addPath(path)
    ctx.clip()
    src.draw(in: rect.insetBy(dx: -6, dy: -6), from: .zero, operation: .sourceOver, fraction: 1)
    ctx.restoreGState()
}
composite.unlockFocus()

// Emit the iconset and let iconutil pack the icns.
let tmp = NSTemporaryDirectory() + "icns-" + UUID().uuidString + ".iconset"
try! FileManager.default.createDirectory(atPath: tmp, withIntermediateDirectories: true)
guard let tiff = composite.tiffRepresentation, let rep = NSBitmapImageRep(data: tiff) else { exit(1) }

for (name, px) in [("icon_16x16", 16), ("icon_16x16@2x", 32), ("icon_32x32", 32), ("icon_32x32@2x", 64),
                   ("icon_128x128", 128), ("icon_128x128@2x", 256), ("icon_256x256", 256),
                   ("icon_256x256@2x", 512), ("icon_512x512", 512), ("icon_512x512@2x", 1024)] {
    let size = NSSize(width: px, height: px)
    let scaled = NSImage(size: size)
    scaled.lockFocus()
    NSGraphicsContext.current?.imageInterpolation = .high
    rep.draw(in: NSRect(origin: .zero, size: size))
    scaled.unlockFocus()
    if let t = scaled.tiffRepresentation, let r = NSBitmapImageRep(data: t),
       let png = r.representation(using: .png, properties: [:]) {
        try! png.write(to: URL(fileURLWithPath: "\(tmp)/\(name).png"))
    }
}

let task = Process()
task.launchPath = "/usr/bin/iconutil"
task.arguments = ["-c", "icns", tmp, "-o", args[2]]
task.launch(); task.waitUntilExit()
try? FileManager.default.removeItem(atPath: tmp)
exit(task.terminationStatus)
