# WinDictate - Quick start

## 0. Before you start

- **Windows 11** (x64): the speech engine's native runtime needs Windows 11 or Windows Server 2022 or newer.
- **Microsoft Visual C++ Redistributable for Visual Studio 2022 (x64)**: from Microsoft's [latest supported Visual C++ Redistributable downloads](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist?view=msvc-170#latest-microsoft-visual-c-redistributable-version) page, if it is not installed already.
- **A CPU with AVX2** (plus AVX, FMA and F16C): Intel Core and AMD Ryzen processors from the last decade have them; some low-end Celeron, Pentium and Atom chips do not.

## 1. Run it

1. Unzip `WinDictate-win-x64.zip`.
2. Double-click `WinDictate.exe`.
3. A blue microphone icon appears in your system tray (bottom-right, near the clock). That is the whole app - there is no main window.

If Windows SmartScreen warns about an unknown publisher, click **More info →
Run anyway**. The build is unsigned at this stage (code-signing is an enterprise
deployment step).

## 2. First dictation

1. Open Notepad or Outlook and click into a text box.
2. **Hold Ctrl + Alt.** A small glass pill appears just above the taskbar once the microphone is recording, and its bars move with your voice (with some Bluetooth headsets it first says "Opening microphone" for a moment - wait for the bars before you speak).
3. Say a sentence: *"This is my first dictation test."*
4. **Release Ctrl + Alt.** The pill shrinks to three dots while it works. After a moment the text is typed where your cursor was, followed by a space so the next dictation does not run into it, and the pill shows a tick and fades.

You can hold the hotkey again straight away, even while the previous dictation is still being transcribed: results are inserted in the order you spoke them. Stay in the same place until the text appears: if you switch to another window (or another field, in apps where Windows can tell) before a dictation is ready, it goes to the clipboard instead of being typed there. In a browser or Teams WinDictate cannot tell one field from another, so wait for the text before clicking elsewhere.

The very first time, the app downloads the speech model (~466 MB for the default
`small.en`). The tray icon's tooltip shows download progress, and if you dictate
before it has finished the pill says "Getting ready". This happens once.

## 3. Settings

Right-click the tray icon → **Settings…** (or double-click the icon).

The line at the top says where things stand: "Ready. Everything stays on this PC." It changes if the speech model still has to download, or if AI cleanup is on (it then names where the text is sent). Nothing changes until you press **Save**.

**Dictation**

- **Shortcut** - the keys you hold to talk (Ctrl + Alt unless changed under Advanced).
- **Indicator** - how it shows it is listening: *Glass pill* (a small capsule above the taskbar with moving bars) or *Edge glow* (the taskbar edge lights up, with no shape). **Preview** plays a pretend dictation so you can see it. Either way it shows words only when something needs your attention.
- **Space after each dictation** (on by default) - keeps consecutive dictations apart.

**This PC**

- **Start with Windows** (off until you switch it on) - WinDictate is ready as soon as you sign in. It adds one entry to your own Windows startup list and removes it when you switch it off.
- **Microphone** - shows the microphone in use. **Test** listens for a few seconds and moves the bar when it hears you; nothing is recorded or kept.
- **Speech quality** - *Fast*, *Balanced* (the default, right for most laptops) or *Accurate* (slower). The line underneath says if a one-time download is still needed.

**Privacy**

- **Keep the text on the clipboard** (on by default) - the dictation stays on the clipboard, so if the cursor wasn't in a text box (or you want the same text again somewhere else) just press **Ctrl+V**. Switch it off to have your previous clipboard put back instead.
- **Save a history of dictations** (off by default) - when off, nothing you say or dictate is written to disk.

**Advanced** (the link at the bottom left)

- **Shortcut keys** - pick the two keys to hold.
- **Ignore quick taps** - holds shorter than this do nothing, so brushing the keys is harmless.
- **Insert text by** - *Pasting* is fastest: it puts the text on the clipboard and presses Ctrl+V for you. *Typing* simulates keystrokes and never uses the clipboard (use it if a clipboard manager interferes).
- **Microphone to use** - choose a specific input if you have several. *System default* follows Windows, for example when you plug in a headset.
- **Speech model** - the exact model behind the quality choice, plus the smallest and largest ones. See the table in the README.
- **Custom words** - comma-separated names and acronyms to listen for.
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
- **"Model not ready, check log"** right after start-up - usually the Visual C++ Redistributable is missing or the CPU lacks AVX2 (see *Before you start*).
- **"No microphone found"** - grant microphone permission (Windows Settings → Privacy → Microphone) and pick the right device in Settings.
- **"Microphone wasn't ready"** - you released the hotkey before the microphone had started (common with Bluetooth headsets). Wait for the moving bars before speaking.
- **"Microphone not responding"** - the microphone driver did not open the device within 10 seconds. Reconnect the headset or pick another microphone in Settings.
- **"Didn't catch that"** - you released too quickly, or the mic picked up no voice at all. Hold a touch longer and speak up.
- **"Copied. Press Ctrl+V"** - the text could not be typed into the focused window (for example Windows rejected the keystrokes, or a key such as Shift stayed held down for several seconds), so it was left on the clipboard. Click where it should go and press Ctrl+V. A dictation finished just before you locked the PC is handled the same way. For the next half minute (or until you copy something else), later dictations are typed rather than pasted so that this text stays on the clipboard; after that the clipboard is used as normal again, so paste it promptly.
- **"Focus moved. Press Ctrl+V"** - you switched window (or field) before the dictation was ready, so it was not typed into the new place. Go back and press Ctrl+V.
- **"Partly typed. Check, then Ctrl+V"** - Windows stopped accepting keystrokes part-way through (for example a UAC prompt appeared). Delete the partial text and press Ctrl+V.
- **"Check it arrived. If not, Ctrl+V"** - Windows accepted only part of the paste keystroke, so the text may or may not have gone in. Look before pressing Ctrl+V, so you do not paste it twice.
- **Changed the model but it cannot download** (offline) - dictation keeps working with the model that was already loaded; the tray shows "Model not ready, check log" after saving and the log records the failed download. Save again once you are back online.
- **"Cancelled" when you did not cancel** - Windows took the keyboard while you were holding the hotkey (the Ctrl+Alt+Del screen or a UAC prompt, for example), so the recording was discarded rather than inserted somewhere unexpected.
- **The chosen microphone is unplugged** - WinDictate records from the Windows default instead and goes back to your microphone when it is connected again; Settings shows it as "(not connected)".
- **Text goes to the wrong window** - text is inserted into whichever window has focus when it is ready. Keep the intended app in front until the tick appears.
- **Logs** - tray menu → **Open log**, or `%APPDATA%\WinDictate\windictate.log`. No transcript text is logged unless you enable history.
