# Claude Color Coded

Color-code the group headings in the Claude desktop app's sidebar. Right-click a heading, choose **Select color** for its highlight or **Select font color** for its name, and pick one of eight colors, any custom color, or a two-color gradient.

Windows only. This is an unofficial community project, not made by or affiliated with Anthropic.

## Install

You need 64-bit Windows and the Claude desktop app. There's nothing else to install: the build uses the C# compiler that comes with Windows.

```powershell
git clone https://github.com/CodySpooner/Claude-Color-Coded.git
cd Claude-Color-Coded
.\build.cmd
.\ClaudeColorCoded.exe
```

It has no window of its own, only an icon in the system tray. To quit, right-click that icon.

To start it with Windows, press Win+R, type `shell:startup`, and put a shortcut to `ClaudeColorCoded.exe` in the folder that opens.

## Use it

Open the Code tab with the sidebar showing, then right-click a group heading. **Select font color** and **Select color** rows appear at the top of the app's menu. Click one and choose:

- One of the eight colors
- **Custom color…** to open a color picker
- **Gradient…** to pick a left and a right color
- **No color** to remove it

Font colors apply to group names only, not to the sessions under them.

## How it works

This is a separate program, not a plugin, and it doesn't change the app's files.

- **Reading the sidebar:** it finds each heading's name and position through Windows UI Automation, the API screen readers use. The app's accessibility tree also holds chat text. That text is only compared against a button label and is never kept.
- **Drawing:** at rest, highlights are tinted strips and names are recolored copies, in its own click-through windows above the sidebar. The color rows are another window, drawn to look like part of the app's menu.
- **Staying attached:** a window drawn over an app always lands a frame or two behind it. So while the sidebar moves, the colors aren't drawn over it at all. Each new frame of the sidebar is read, the colors are painted into that frame, and the result is shown as one picture, so text and color can't separate. Clicks still pass through to the app. When a click expands or collapses a group, the picture waits until the new row positions match what's on screen, then shows both together, so the change appears about 100 ms after the click.
- **Mouse:** a system-wide mouse hook reacts to button presses and releases and to the wheel, to notice right-clicks on headings, clicks and scrolling in the sidebar, and window drags. Nothing is recorded.
- **Screen pixels:** it reads the sidebar's pixels for about a third of a second after each scroll or click in the sidebar, the open menu's pixels to match its colors, and each colored name's pixels with Claude in front to draw it. They're held in memory and never saved.
- **Saved data:** only group names and their colors, in `%LOCALAPPDATA%\ClaudeColorCoded\colors.txt` and `font-colors.txt`. It makes no network connections.

## Limits

- It relies on the app's English `New session in <name>` button label and one of its class names, so another display language or an app update can break it. To see whether it can still read your headings, run this. It lists them, or says what failed:

  ```powershell
  .\ClaudeColorCoded.exe --check | Out-String
  ```

- It has only been tested on Windows 11 in dark mode. Sizes assume 100% display scaling, and scrolling was tested on a 165 Hz screen.
- Screen recordings and screenshots taken during a scroll, or just after a click in the sidebar, don't show the colors. Our windows are hidden from capture for that moment so they aren't read back into the picture. At rest they show as normal.
- On a rotated screen it uses an older, slower way of reading the screen, so scrolling there may look less smooth. That way was tested on a normal screen, where colors stayed attached, but not on a rotated one.
- A heading that newly appears can take up to 5 seconds to get its colors, or until the next click that moves rows in the sidebar.
- HDR hasn't been tested. The picture's colors could differ slightly from the resting ones there, showing as a small change when scrolling starts and stops.
- A colored name is first drawn once Claude is in front with its sidebar still, so a new font color may wait until then.
- If you click in the sidebar and turn the wheel within about 100 ms, the colors can be off until that scroll stops.
- The app's in-window pop-ups aren't detected, so one that overlaps the sidebar has the colors drawn on top of it.
- After you pick a color, the app's menu usually closes by itself. In testing it occasionally stayed open, and clicking elsewhere closes it.
- The color rows open on click rather than hover, and the app's arrow keys can't reach them.
- For a heading at the very top of the screen, the color rows cover part of the app's first menu item. Scrolling down a little frees it.
- The color picker is mouse-only, so you can't type a hex code.
- Colors follow a group's name, so a group whose name changes loses its colors.
- While the sidebar straddles two screens, the part on the other screen has no colors.

## License

[MIT](LICENSE)
