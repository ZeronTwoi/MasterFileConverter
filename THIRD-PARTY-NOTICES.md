# Third-party notices

Master File Converter itself is published under the MIT License (see `LICENSE`).

Its downloads contain the libraries listed here, each under its own licence. This file names
them; the longer licence texts are separate files. In a download they all lie in the same
folder as this file (`app/licenses`). In the source repository the ones marked *package* come
with the library's NuGet package and are copied into the download when it is built; the others
are in `build/licenses`.

## In the downloads

| Library | Used for | Licence | Notice or licence text |
|---|---|---|---|
| [.NET runtime](https://github.com/dotnet/runtime) | running the app | MIT | *package:* `Microsoft.NETCore.App.Runtime.*` |
| [Avalonia](https://github.com/AvaloniaUI/Avalonia) | the window | MIT | below |
| [MicroCom](https://github.com/kekekeks/MicroCom) (Windows) | part of Avalonia | MIT | below |
| [Tmds.DBus](https://github.com/tmds/Tmds.DBus) (Linux) | part of Avalonia | MIT | below |
| [ANGLE](https://chromium.googlesource.com/angle/angle) (Windows) | part of Avalonia: drawing | BSD 3-Clause | *package:* `Avalonia.Angle.Windows.Natives` |
| [Inter](https://github.com/rsms/inter) (Linux) | the window's font | SIL Open Font License 1.1 | `OFL-1.1-Inter.txt` |
| [SkiaSharp](https://github.com/mono/SkiaSharp) and [Skia](https://skia.org) | drawing, picture → PDF | MIT, BSD 3-Clause | *package:* `SkiaSharp`, `SkiaSharp.NativeAssets.*` |
| [HarfBuzzSharp](https://github.com/mono/SkiaSharp) and [HarfBuzz](https://github.com/harfbuzz/harfbuzz) | text layout | MIT | *package:* `HarfBuzzSharp`, `HarfBuzzSharp.NativeAssets.*` |
| [.NET Community Toolkit](https://github.com/CommunityToolkit/dotnet) | the window's logic | MIT | *package:* `CommunityToolkit.Mvvm` |
| [Magick.NET](https://github.com/dlemstra/Magick.NET) | images | Apache 2.0 | *package:* `Magick.NET-Q16-AnyCPU` |
| [ImageMagick](https://imagemagick.org) and the libraries built into it (libjpeg-turbo, libpng, libwebp, libtiff, libheif, libde265, libjxl, libraw, OpenJPEG, Little CMS, FreeType, zlib and others) | images | ImageMagick License; LGPL for libheif, libde265 and libraw; BSD, MIT, zlib and similar for the rest | *package:* `Magick.NET-Q16-AnyCPU` |
| [PDFium](https://pdfium.googlesource.com/pdfium/), built by [pdfium-binaries](https://github.com/bblanchon/pdfium-binaries), and the libraries built into it | PDF → images | BSD 3-Clause, Apache 2.0, MIT | `PDFium.txt` |
| [QuestPDF](https://www.questpdf.com) | text → PDF | QuestPDF Community License | *package:* `QuestPDF` |
| [qpdf](https://github.com/qpdf/qpdf) | part of QuestPDF | Apache 2.0 | `qpdf.md`, `Apache-2.0.txt` |
| [Lato](https://www.latofonts.com) | part of QuestPDF: the font of PDFs made from text | SIL Open Font License 1.1 | `OFL-1.1-Lato.txt` |
| [PdfPig](https://github.com/UglyToad/PdfPig) | reading text from PDFs | Apache 2.0 | `Apache-2.0.txt` |
| [Open XML SDK](https://github.com/dotnet/Open-XML-SDK) | Word documents | MIT | below |
| [SharpCompress](https://github.com/adamhathcock/sharpcompress) | archives | MIT | below |

QuestPDF is free of charge for open-source projects published under an OSI-approved licence,
which this one is. Its licence is not itself an open-source licence: whoever builds a product
of their own from this source code should read QuestPDF's terms for themselves.

## Not in the downloads

These programs are not part of Master File Converter and are not distributed with it. The app
uses them when they are installed, and offers to fetch the first two from their own publishers:

| Program | Used for | Licence | Fetched from |
|---|---|---|---|
| [FFmpeg](https://ffmpeg.org) | audio and video | GPL 3 (the builds named here) | [gyan.dev](https://www.gyan.dev/ffmpeg/builds/) on Windows, [BtbN](https://github.com/BtbN/FFmpeg-Builds) on Linux |
| [7-Zip](https://www.7-zip.org) | .7z, password-protected zip | LGPL 2.1, with the unRAR restriction for its RAR code | [7-zip.org](https://www.7-zip.org) / [its GitHub releases](https://github.com/ip7z/7zip/releases) |
| LibreOffice, Microsoft Word, WinRAR / RAR | documents, creating .rar | their own | never fetched; used only if installed |

## Copyright notices of the MIT-licensed libraries

- Avalonia — Copyright (c) AvaloniaUI OÜ. All Rights Reserved.
- MicroCom — Copyright (c) 2021 Nikita Tsukanov
- Tmds.DBus — Copyright 2006 Alp Toker, Copyright 2010 Other Contributors, Copyright 2016 Tom Deseyn
- Open XML SDK — Copyright (c) .NET Foundation and Contributors. All rights reserved.
- SharpCompress — Copyright (c) 2014 Adam Hathcock

each under these terms:

```text
Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
