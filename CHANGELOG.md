# What changed in each version

The part for a version is also what its page under
[Releases](https://github.com/ZeronTwoi/MasterFileConverter/releases) says.

## 2.5.0

Many more conversions — documents, spreadsheets, presentations and drawings, to and from PDF and
among each other.

- **Documents:** RTF, HTML, OpenDocument text (.odt) and e-books (.epub) are new, and Markdown is
  read as Markdown. Every text document becomes every other, a PDF, or pictures of its pages.
- **The built-in converter keeps a document's structure** — headings, bold and italic, lists
  inside lists, tables, pictures, links — where it used to carry over the text alone. A Word file
  becomes a web page or an e-book with everything in place; a Markdown file a properly set PDF
  or a Word document with real heading styles. It needs nothing installed.
- **Spreadsheets:** Excel (.xlsx, and the old .xls as a source), OpenDocument (.ods) and CSV
  into each other, into PDF and a web page. Numbers stay numbers, dates stay dates.
- **Presentations:** PowerPoint (.pptx, and the old .ppt as a source) and OpenDocument (.odp)
  into each other, into PDF and into pictures of the slides.
- **Out of a PDF:** into Word, OpenDocument, RTF, HTML, Markdown and EPUB with headings,
  paragraphs and pictures found again; into Excel, ODS or CSV with its tables found again; into
  PowerPoint or ODP with every page a slide that shows it exactly; into one TIFF of all its
  pages; into SVG, as a real drawing — shapes as shapes, letters as outlines.
- **SVG is read:** into every picture format, drawn sharp at 300 dpi (or the resolution you
  set), and into a PDF as a drawing rather than a picture of one.
- **A TIFF of several pages** (a scan, a fax) keeps all its pages: into a PDF, or as pictures.
- **Excel and PowerPoint work for the app** the way Word did, where Microsoft Office is
  installed, and LibreOffice does for all three kinds of file. They lay a document out exactly
  as it is and are asked first; the built-in converter stands in where none is installed.
  OpenDocument drawings (.odg) and Publisher files (.pub) become PDFs through LibreOffice.
- **Safer with office files:** macros in a file are never run while Word, Excel or PowerPoint
  convert it, and no hidden office program is left running afterwards. A PowerPoint you have
  open yourself is used, and left open.
- Nothing is fetched from the internet while a web page or Markdown file is converted: pictures
  come from the file or its own folder.
- Fixed: Word could wait four minutes on a PDF when two batches followed closely on each other.
- `--self-test --engine builtin|libreoffice|office` checks the document conversions with one
  engine alone.

## 2.4.0

- **Updates install themselves.** When a new version is out, one click on the warning sign
  downloads it, checks it against its checksum and installs it; the app comes back by itself.
  With files in the list it waits for *Restart and update* instead of closing on you — or
  installs when you close the app. Nothing is ever downloaded or installed without that click.
  From a terminal: `--update`, or `--update --from <file>` with a download that is already
  there, for a machine that is not online.
- **A look for a new version on request** (··· menu → *Look for a new version now*), for those
  who do not want the app to look by itself.
- **Uninstalling removes everything:** the app, its shortcuts and registry entries, the engines
  it downloaded, a taskbar pin, leftover work files — and your settings, unless you choose to
  keep them. On Linux: `masterfileconverter --uninstall`. The uninstaller asks you to close the
  app first if it is still open, instead of leaving files behind.
- **The installer** makes the Start menu shortcut a choice like the others, says on its first
  page what it does and does not put on the computer, closes a running copy of the app before
  replacing it, and shows the app's logo in Windows' dark mode too.
- **Closing the window ends everything.** A conversion that is still running is stopped (after
  asking) and its half-written file removed; no encoder keeps running after the window is gone.
- Work files lie in the temp folder under the app's name, and ones a crash left behind are
  cleared away at a later start.
- ··· menu → *Website and source code*.

## 2.3.0

- The app can tell you when a new version is out — only if you say yes when it asks, once.
- The Windows installer has proper pages: the licence, where to install, what to add, a summary.

## 2.2.0

- First public release: MIT licence, a Windows installer, and downloaded engines checked against
  their publishers' checksums.
