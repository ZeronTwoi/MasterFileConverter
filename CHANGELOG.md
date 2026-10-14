# What changed in each version

The part for a version is also what its page under
[Releases](https://github.com/ZeronTwoi/MasterFileConverter/releases) says.

## 2.7.0

The files selected set to one format at once.

- **Set them to.** With several files selected, a box under the list sets their target format
  in one go, as *Set all to* does for every file — the other files, and files added later, keep
  theirs. A format only some of the selected files can become says how many ("pdf (2 of 3)"),
  and the rest keep the format they have. The selection stays, so its settings can be set next.

## 2.6.1

Groups that do what they say.

- **What is selected is what is set.** Format settings open on the files selected, so a change
  made there is theirs alone. Every file of a format is set only when *Every file converted to
  X, now and later* is chosen — before, that was where the panel opened, so a change meant for
  two files went to every file of the format, to the files added afterwards, and to the next
  session.
- **Only files given settings of their own are in a group.** Files not touched, and files added
  later, carry no group tag. What every file of a format gets is said with a plain *JPG
  settings* mark instead, which a click opens.
- **Group numbers stay put.** A group keeps its number and colour as long as it is there — when
  files are added or removed, when another group is made, when its settings are changed for all
  its files at once. A file reset, or set back to its format's way, leaves its group instead of
  keeping the tag.
- **A file's own settings stay with the format they were made for:** set to another format, it
  has none of them there; set back, it finds them again.
- **A click on a group's tag selects the whole group,** with its settings in the panel.
- **The panel's Group list** puts the files selected in a group, giving them its settings, or in
  none.
- **Combining keeps groups apart.** With *Combine into one file*, each group becomes a file of
  its own with its own settings — before, files' own settings were dropped when they were
  combined. Two combined results that would get the same name no longer can.

## 2.6.0

Compress a file in its own format, set several files at once, and a plainer speed setting.

- **The same format, only compressed.** A picture, sound, video, zip, 7z or rar can be set to
  its own format — a JPEG to JPEG — just to be made smaller by the File size slider or its format
  settings. A (!) in the row says so. The source is never replaced, and a file that cannot be
  made any smaller is kept exactly as it was rather than saved again larger.
- **A file size of its own.** In Format settings, *Set by: Own file size* gives a format, or the
  files selected, their own File size; the slider at the bottom then leaves them alone and says
  so ("Not for 2 of 5 JPG files").
- **Several files at once.** Shift-click selects a range, Ctrl-click adds a file or takes it away,
  and holding Shift or Ctrl while dragging over the list does the same to every row passed over.
  Files converted the same way are then set together in Format settings.
- **Groups in colour.** Files with exactly the same format settings carry the same coloured
  *Group* tag, whose tooltip lists their settings — so one group can be made small for e-mail
  and another kept large, side by side.
- **Result instead of Speed, Auto and Use GPU.** One slider from *Quick* to *Best* says in plain
  words what the speed setting does; Speed, Auto and Use GPU are folded away under *Speed
  details* for those who want them.
- A TIFF of several pages set to TIFF stays one file with all its pages.

## 2.5.1

Clearer settings: two places with names that say what they are for, and nothing that can be set
in both.

- **Settings for all files** is the bar at the bottom: file size, speed, where results go. It
  now says so above it.
- **Format settings** is the new name of the *Detailed settings* button and panel: what only
  the selected file's target format has — resolution, codec, pages, password and more.
- **Size and quality are set in one place at a time.** The panel's *Set by* choice is the File
  size slider unless you pick, for that format alone, a fixed quality, a fixed bitrate or an
  exact file size — only that box is shown, and choosing one clears the others, so they never
  override each other unseen. Under the slider the bar names the formats it does not decide.
- The panel no longer has a second speed control (video encoding speed, PNG and FLAC
  compression effort): the Speed setting decides them. A script can still set them with `--set`.
- Settings saved by 2.5.0 keep doing what they did: where several sizes or qualities were set
  for one format, the one that won then is the one chosen now.
- The panel can no longer reach over the bar at the bottom in a short window.

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
