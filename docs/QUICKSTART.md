# WinDictate - Quick start

## 1. Run it

1. Unzip `WinDictate-win-x64.zip`.
2. Double-click `WinDictate.exe`.
3. A blue microphone icon appears in your system tray (bottom-right, near the clock). That is the whole app - there is no main window.

If Windows SmartScreen warns about an unknown publisher, click **More info →
Run anyway**. The build is unsigned at this stage (code-signing is an enterprise
deployment step).

## 2. First dictation

1. Open Notepad or Outlook and click into a text box.
2. **Hold Ctrl + Alt.** A small "Listening…" pill appears at the bottom of the screen.
3. Say a sentence: *"This is my first dictation test."*
4. **Release Ctrl + Alt.** After a moment the text is typed where your cursor was.

The very first time, the app downloads the speech model (~466 MB for the default
`small.en`). The overlay/tray will show download progress. This happens once.

## 3. Settings

Right-click the tray icon → **Settings…** (or double-click the icon).

- **Hotkey** - pick the modifier combo to hold. Default Ctrl + Alt.
- **Speech model** - smaller = faster, larger = more accurate. See the table in the README.
- **Microphone** - choose a specific input if you have several.
- **Text insertion**:
  - *Paste* - fastest; puts the transcript on the clipboard and presses Ctrl+V for you.
  - *Type* - simulates keystrokes (use this if clipboard managers interfere).
  - *Keep transcript on the clipboard afterwards* (on by default) - the dictation stays on the clipboard, so if the cursor wasn't in a text box (or you want the same text again somewhere else) just press **Ctrl+V**. Untick it to have paste mode restore your previous clipboard instead.
- **Custom words / acronyms** - comma-separated terms (e.g. company names, product names) to bias recognition.
- **AI cleanup** - optional; off by default. See below.

## 4. Optional: AI cleanup

WinDictate transcribes locally. If you want punctuation/formatting polished by an
LLM, enable cleanup and provide an **OpenAI-compatible** endpoint:

- **Public OpenAI:** Base URL `https://api.openai.com/v1`, model e.g. `gpt-4o-mini`, and your API key.
- **Internal gateway:** Base URL of your organisation's approved endpoint, the model name it exposes, and a key if it needs one (leave blank if it does not).

If cleanup is unreachable or slow, WinDictate silently falls back to the raw local
transcript, so dictation never breaks.

## 5. Air-gapped machines (no internet)

The app fetches models from Hugging Face on first use. If the machine has no
internet:

1. On any connected machine, download the model file, e.g.
   `https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.en.bin`
2. Copy it to `%APPDATA%\WinDictate\models\` on the target machine
   (create the folder if needed). The filename must stay `ggml-small.en.bin`.
3. Start WinDictate and select that model in Settings.

## Troubleshooting

- **Nothing happens on the hotkey** - check the tray icon is present and "Enabled" is ticked in its menu. Some endpoint-security tools block low-level keyboard hooks; the log will note if the hook failed to install.
- **"No microphone"** - grant microphone permission (Windows Settings → Privacy → Microphone) and pick the right device in Settings.
- **"No speech detected"** - you released too quickly, or the mic level was silent. Hold a touch longer and speak up.
- **Text goes to the wrong window** - the overlay shows the target window title. Make sure the intended app has focus before holding the hotkey.
- **Logs** - tray menu → **Open log**, or `%APPDATA%\WinDictate\windictate.log`. No transcript text is logged unless you enable history.
