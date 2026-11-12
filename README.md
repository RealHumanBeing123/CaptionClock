# CaptionClock

CaptionClock is a small offline Windows subtitle timing editor. It edits SubRip (`.srt`) and WebVTT (`.vtt`) captions and can preview a local video while you adjust caption timing.

## Features

- Open local video files and play, pause, and seek through the video.
- Import and export `.srt` and `.vtt` subtitle files.
- Edit start time, end time, and caption text in a table.
- Set a selected caption's start or end to the current video playhead.
- Shift a selected caption earlier or later by 100 milliseconds.
- Preview the selected subtitle over the video and drag it to reposition it. During playback, the preview follows cue timing.
- Choose an installed Windows font or import a local `.ttf` / `.otf` font.
- Set font size, bold, italic, underline, left / center / right alignment, and a black or white text outline.
- Paste and edit Unicode text, including non-English characters. Subtitle exports are written as UTF-8.
- Save a `.ccproj` project to preserve appearance, placement, video path, and cue timing. Imported fonts are copied into a sibling `<project>_fonts` folder when the project is saved.
- Add and delete captions.
- Works without a network connection after installation/build.

Video formats depend on the media codecs available in Windows. The app does not upload or send media anywhere.

SRT and VTT are subtitle exchange formats, so they cannot preserve every CaptionClock appearance setting. Exports retain text emphasis; VTT also stores cue placement. The `.ccproj` file is the editable format for custom fonts, font size, outline, and exact preview placement. Keep the project file and its `_fonts` folder together.

## Build from Command Prompt

Install the **.NET 10 SDK** on Windows, open Command Prompt in this folder, and run:

```bat
build.bat
```

The self-contained executable is written to `publish\CaptionClock.exe`. The machine that runs that executable does not need the .NET runtime installed. The first publish may need internet access to download the .NET runtime pack; the built app itself works offline.

If you double-click `build.bat`, the window stays open when the build finishes. Build output is also saved to `build.log`. The `publish` folder is created by a successful publish; it is not present in the source package beforehand.

You can also build directly:

```bat
dotnet publish CaptionClock.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o publish
```

To run from source while developing, use `run.bat` or `dotnet run --project CaptionClock.csproj`. `run.bat` launches `publish\CaptionClock.exe` when it exists. Otherwise, it runs from source and keeps the console open; startup output is saved to `run.log`.

## Time format

Enter times as `HH:MM:SS,mmm` (for example, `00:01:23,450`). `MM:SS,mmm` is accepted too. End time must be later than start time.
