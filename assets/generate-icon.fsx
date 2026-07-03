// Generates a 256x256 RGBA PNG package icon: a white git-branch glyph on an indigo
// rounded square. Pure managed code — supersampled rasterizer + hand-rolled PNG encoder
// (ZLibStream for IDAT, manual CRC32). No System.Drawing / external image deps.
open System
open System.IO
open System.IO.Compression

let W = 256          // output size
let SS = 4           // supersampling factor (4x4 subsamples per output pixel)

// Brand indigo #4F46E5, white glyph.
let bgR, bgG, bgB = 79.0, 70.0, 229.0
let fgR, fgG, fgB = 255.0, 255.0, 255.0

let inline clampf (v: float) lo hi = max lo (min hi v)

// Rounded-rect membership in W-space [0,W] with corner radius r.
let inRoundedRect (x: float) (y: float) (r: float) : bool =
    let w = float W
    let inXBand = x >= r && x <= w - r
    let inYBand = y >= r && y <= w - r
    if (inXBand && y >= 0.0 && y <= w) || (inYBand && x >= 0.0 && x <= w) then
        true
    else
        // corner circles
        let cornerCentre =
            [ (r, r); (w - r, r); (r, w - r); (w - r, w - r) ]
            |> List.exists (fun (cx, cy) ->
                let dx, dy = x - cx, y - cy
                dx * dx + dy * dy <= r * r)
        cornerCentre

// Distance from point (px,py) to segment (ax,ay)-(bx,by).
let distSeg px py ax ay bx by =
    let dx, dy = bx - ax, by - ay
    let l2 = dx * dx + dy * dy
    let t = if l2 = 0.0 then 0.0 else clampf (((px - ax) * dx + (py - ay) * dy) / l2) 0.0 1.0
    let cx, cy = ax + t * dx, ay + t * dy
    sqrt ((px - cx) * (px - cx) + (py - cy) * (py - cy))

let dist px py cx cy =
    sqrt ((px - cx) * (px - cx) + (py - cy) * (py - cy))

// Glyph geometry in W-space (a git branch: a trunk with two commits, one branch commit).
let nodeR = 16.0
let lineR = 6.0
// trunk
let ax, ay = 94.0, 74.0
let bx, by = 94.0, 182.0
// branch
let branchStartX, branchStartY = 94.0, 128.0
let cx3, cy3 = 164.0, 86.0

let inGlyph (x: float) (y: float) : bool =
    dist x y ax ay <= nodeR
    || dist x y bx by <= nodeR
    || dist x y cx3 cy3 <= nodeR
    || distSeg x y ax ay bx by <= lineR
    || distSeg x y branchStartX branchStartY cx3 cy3 <= lineR

// Render each output pixel by averaging SS*SS subsamples.
let pixels = Array.zeroCreate<byte> (W * W * 4)
for oy in 0 .. W - 1 do
    for ox in 0 .. W - 1 do
        let mutable sr, sg, sb, sa = 0.0, 0.0, 0.0, 0.0
        for sy in 0 .. SS - 1 do
            for sx in 0 .. SS - 1 do
                let x = float ox + (float sx + 0.5) / float SS
                let y = float oy + (float sy + 0.5) / float SS
                if not (inRoundedRect x y 46.0) then
                    () // transparent
                elif inGlyph x y then
                    sr <- sr + fgR
                    sg <- sg + fgG
                    sb <- sb + fgB
                    sa <- sa + 255.0
                else
                    sr <- sr + bgR
                    sg <- sg + bgG
                    sb <- sb + bgB
                    sa <- sa + 255.0
        let n = float (SS * SS)
        // Composite over the averaged alpha: colours are premultiplied by contribution already,
        // so divide by the number of *covered* subsamples for colour, by total for alpha.
        let covered = sa / 255.0
        let idx = (oy * W + ox) * 4
        if covered = 0.0 then
            pixels.[idx] <- 0uy
            pixels.[idx + 1] <- 0uy
            pixels.[idx + 2] <- 0uy
            pixels.[idx + 3] <- 0uy
        else
            pixels.[idx] <- byte (Math.Round(sr / covered))
            pixels.[idx + 1] <- byte (Math.Round(sg / covered))
            pixels.[idx + 2] <- byte (Math.Round(sb / covered))
            pixels.[idx + 3] <- byte (Math.Round(sa / n))

// ---- PNG encoding ----
let crcTable =
    Array.init 256 (fun n ->
        let mutable c = uint32 n
        for _ in 0 .. 7 do
            c <- if c &&& 1u <> 0u then 0xEDB88320u ^^^ (c >>> 1) else c >>> 1
        c)

let crc32 (data: byte[]) =
    let mutable c = 0xFFFFFFFFu
    for b in data do
        c <- crcTable.[int ((c ^^^ uint32 b) &&& 0xFFu)] ^^^ (c >>> 8)
    c ^^^ 0xFFFFFFFFu

let be32 (v: uint32) = [| byte (v >>> 24); byte (v >>> 16); byte (v >>> 8); byte v |]

let chunk (typ: string) (data: byte[]) =
    use ms = new MemoryStream()
    ms.Write(be32 (uint32 data.Length), 0, 4)
    let typeBytes = Text.Encoding.ASCII.GetBytes typ
    ms.Write(typeBytes, 0, 4)
    ms.Write(data, 0, data.Length)
    let crcInput = Array.append typeBytes data
    ms.Write(be32 (crc32 crcInput), 0, 4)
    ms.ToArray()

// IHDR
let ihdr =
    Array.concat
        [ be32 (uint32 W)
          be32 (uint32 W)
          [| 8uy; 6uy; 0uy; 0uy; 0uy |] ] // bitdepth 8, colortype 6 (RGBA), deflate, filter 0, no interlace

// Raw image data: each scanline prefixed with filter byte 0.
let raw =
    use ms = new MemoryStream()
    for y in 0 .. W - 1 do
        ms.WriteByte 0uy
        ms.Write(pixels, y * W * 4, W * 4)
    ms.ToArray()

let idatData =
    use ms = new MemoryStream()
    (use z = new ZLibStream(ms, CompressionLevel.Optimal, true)
     z.Write(raw, 0, raw.Length))
    ms.ToArray()

let signature = [| 137uy; 80uy; 78uy; 71uy; 13uy; 10uy; 26uy; 10uy |]

let png =
    Array.concat
        [ signature
          chunk "IHDR" ihdr
          chunk "IDAT" idatData
          chunk "IEND" [||] ]

let outPath = Path.Combine(__SOURCE_DIRECTORY__, "icon.png")
File.WriteAllBytes(outPath, png)
printfn "wrote %s (%d bytes, %dx%d RGBA)" outPath png.Length W W
