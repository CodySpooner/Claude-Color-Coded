# Claude Color Coded

Color-code the group headings in the Claude desktop app's sidebar. Right-click a heading, choose **Select color**, and pick one of eight colors, any custom color, or a two-color gradient.

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

Open the Code tab with the sidebar showing, then right-click a group heading. A **Select color** row appears at the top of the app's menu. Click it and choose:

- One of the eight colors
- **Custom color…** to open a color picker
- **Gradient…** to pick a left and a right color
- **No color** to remove the highlight

## How it works

This is a separate program, not a plugin, and it doesn't change the app's files.

- **Reading the sidebar:** it finds each heading's name and position through Windows UI Automation, the API screen readers use. The app's accessibility tree also holds chat text. That text is only compared against a button label and is never kept.
- **Drawing:** the highlights are tinted strips in its own click-through window above the sidebar. The Select color row is another window, drawn to look like part of the app's menu.
- **Mouse:** a system-wide mouse hook reacts to button presses and the wheel, to notice right-clicks on headings, scrolling and window drags. Nothing is recorded.
- **Screen pixels:** it reads the sidebar's pixels while the sidebar scrolls, to keep the strips lined up, and the open menu's pixels, to match its colors. Neither is saved.
- **Saved data:** only group names and their colors, in `%LOCALAPPDATA%\ClaudeColorCoded\colors.txt`. It makes no network connections.

## Limits

- It relies on the app's English `New session in <name>` button label and one of its class names, so another display language or an app update can break it. To see whether it can still read your headings, run this. It lists them, or says what failed:

  ```powershell
  .\ClaudeColorCoded.exe --check | Out-String
  ```

- It has only been tested on Windows 11 in dark mode. Sizes assume 100% display scaling, and scroll tracking was tuned on a 165 Hz screen.
- After you pick a color, the app's menu usually closes by itself. In testing it occasionally stayed open, and clicking elsewhere closes it.
- The Select color row opens on click rather than hover, and the app's arrow keys can't reach it.
- The color picker is mouse-only, so you can't type a hex code.
- Colors follow a group's name, so a group whose name changes loses its color.
- While the sidebar straddles two screens, the part on the other screen has no strips.

## License

[MIT](LICENSE)
