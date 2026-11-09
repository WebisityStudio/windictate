# WinDictate - Manual test checklist (Windows 11)

The unit tests (`dotnet test`) cover the logic that can run on any OS. Those
are the hotkey state machine and watchdog, the dictation queue and insertion
fallbacks, transcript clean-up, the speech gate, microphone matching and
settings compatibility. This checklist covers what needs a real Windows 11
laptop: the keyboard hook, the microphone, Whisper, the clipboard, SendInput,
the listening indicator, lock, sleep and secure-desktop behaviour.

Allow about 45 minutes. Report anything that does not match **Expected**,
with the time and the matching lines from the log (tray icon → **Open log**).
The log never contains dictated text.

## Before you start

- [ ] The laptop meets the requirements in the README: Windows 11 x64, the
      Visual C++ 2022 x64 Redistributable, and a CPU with AVX2.
- [ ] Copy `%APPDATA%\WinDictate\settings.json` somewhere safe (it is your v0.1.3 file).
- [ ] Build this branch and run it:
      `dotnet publish src/WinDictate/WinDictate.csproj -c Release -r win-x64 --self-contained true -o publish/WinDictate`,
      then start `publish/WinDictate/WinDictate.exe`. Optionally also run `dotnet test`.
- [ ] Open Word or Outlook for some tests. Their **Ctrl+Alt+V** opens *Paste
      Special*, so if that dialog ever appears, a paste was sent while Ctrl+Alt
      was still held. That is a failure.
- [ ] Know where Windows 11 shows the **microphone-in-use icon**: in the taskbar
      corner near the clock, and in *Settings → Privacy & security → Microphone →
      Recent activity*. Several tests use it to confirm the microphone closed.

## 1. Basics and settings compatibility

- [ ] **Existing settings load.** Start with your v0.1.3 `settings.json`.
      Expected: hotkey, model, microphone and insertion mode are as before. In
      Settings, *Add a space after each dictation* is ticked.
- [ ] **Save adds the new keys.** Click Save in Settings. Expected:
      `settings.json` now also has `AddTrailingSpace`, `MicDeviceName` and
      `MinVoicedMs`, and the other values are unchanged.
- [ ] **Basic dictation.** In Notepad, hold Ctrl+Alt, say a sentence, release.
      Expected: the glass pill with bars that move with your voice, then three
      dots, then the text appears, followed by one space, and the pill shows a
      tick and fades. The tray tooltip goes Listening → Transcribing →
      Ready.
- [ ] **Quick tap.** Tap Ctrl+Alt briefly. Expected: nothing appears.
- [ ] **Enabled toggle.** Untick *Enabled* in the tray menu, then try to
      dictate. Expected: nothing happens. Tick it again and dictation works.
- [ ] **Settings text.** The custom-words help text reads "e.g. KYC, ISA,
      onboarding" and names no company.

## 2. Hotkey after a cancel (bug 1)

- [ ] Hold Ctrl+Alt, start speaking, press **Esc**, release everything, then
      hold Ctrl+Alt again straight away. Repeat three times. Expected: every
      second hold shows the pill with moving bars. Before the fix, the hotkey stayed dead
      until a letter was typed.
- [ ] Same with release orders Ctrl-then-Alt and Alt-then-Ctrl.
- [ ] Hold Ctrl+Alt, press **E**, release. Expected: "Cancelled" (or nothing
      for a very short hold); nothing is inserted; the next hold works.
- [ ] *(Polish, German or other AltGr layouts only.)* Type several AltGr
      characters (for example AltGr+A). Expected: no dictation starts, and
      Ctrl+Alt works normally afterwards.

## 3. Brackets (bug 2)

- [ ] Dictate a sentence with a spoken aside, for example "Transfer five
      thousand pounds to the client's ISA, not the current account". Expected:
      if Whisper writes the aside in brackets or parentheses, they stay in the
      text, with no stray space before the full stop.
- [ ] Hold the hotkey for about 3 seconds in silence. Expected: "No speech
      detected". No "[BLANK_AUDIO]" or similar text is inserted.

## 4. Back-to-back dictation and safe insertion (bug 3)

- [ ] In Word, dictate a long sentence (about 10 seconds). Release, and
      immediately hold Ctrl+Alt again and dictate a second sentence. Expected:
      the pill with moving bars appears again straight away. Both sentences are
      inserted, the first one first, separated by a space. *Paste Special*
      never opens.
- [ ] Dictate, release, and **keep Shift held** for about 2 seconds. Expected:
      nothing is inserted while Shift is down. The text arrives when you let go.
- [ ] Dictate, release, and keep Shift held for **more than 5 seconds**.
      Expected: "Copied. Press Ctrl+V". Ctrl+V
      pastes the dictation.
- [ ] Repeat the previous test with *Keep transcript on the clipboard
      afterwards* **unticked**. Expected: the same notice, and Ctrl+V still
      pastes the dictation.
- [ ] With that box unticked, copy some text of your own, dictate normally,
      then press Ctrl+V elsewhere. Expected: your own text is pasted (the
      clipboard was restored).
- [ ] Insertion mode **Type**: dictate into Notepad. Expected: the text is
      typed, followed by a space.
- [ ] **Lock right after dictating.** Dictate a long sentence, release, and
      press Win+L at once. Unlock. Expected: the text is not typed in after the
      unlock. "Copied. Press Ctrl+V" appears after you
      unlock, and Ctrl+V pastes the dictation.
- [ ] **Probe: administrator window.** Open Notepad with *Run as
      administrator*, click into it and hold Ctrl+Alt. Expected: either nothing
      happens (Windows does not pass keys from administrator windows to a
      normal app), or the dictation ends with "Copied. Press Ctrl+V".
      Text must never be lost or garbled.
- [ ] **Probe: focus moves to an administrator window.** Dictate into a normal
      Notepad, release, and within a second click into the administrator
      Notepad. Note what happens, with *Keep transcript on the clipboard*
      ticked and then unticked. Report whether the text arrives, whether the
      pill shows a tick or "Copied. Press Ctrl+V", and whether Ctrl+V still has
      the text. Windows does not always report blocked input, and this is the
      one case that could not be checked here.

## 5. Keyboard hook on its own thread (bug 4)

- [ ] **Model change.** In Settings, switch the model to one that is not
      downloaded (for example `base.en`) and save. While it downloads and then
      loads, type quickly in Notepad, open the tray menu, and drag the
      Settings window. Expected: no typing lag or dropped keys, and the menu
      and window stay responsive. The log shows "Loaded model base.en". Switch
      back afterwards.
- [ ] **Typing during transcription.** Dictate about 20 seconds, then type in
      another app while it transcribes. Expected: no lag.
- [ ] **Lock and unlock.** Press Win+L, unlock, and dictate. Expected: it works.
      The log shows "Re-installing keyboard hook after session SessionUnlock"
      and "Keyboard hook re-installed".
- [ ] **Sleep and resume.** Put the laptop to sleep, wake it, and dictate.
      Expected: it works. The log shows the re-install after "resume".

## 6. Lost key releases and sleep (bug 5)

- [ ] **Ctrl+Alt+Del.** Hold Ctrl+Alt, start speaking, press **Del** while
      holding, then press Esc (or Cancel) on the security screen. Expected:
      nothing is inserted, the microphone-in-use icon disappears within about a
      second, and the next Ctrl+Alt hold works straight away. The log may show
      "Dictation cancelled: the hotkey was no longer held but its release never
      arrived".
- [ ] **UAC prompt while holding.** In PowerShell run
      `Start-Sleep 5; Start-Process notepad -Verb RunAs`. Then click into a
      normal Notepad and hold Ctrl+Alt until the UAC prompt appears. Release on
      the prompt and click **No**. Expected: "Cancelled" (or nothing), nothing is inserted, the microphone icon disappears, and the
      next hold works.
- [ ] **Sleep while recording.** In PowerShell run
      `Start-Sleep 5; rundll32.exe powrprof.dll,SetSuspendState 0,1,0` (this may
      hibernate if hibernation is on). Then hold Ctrl+Alt and keep talking until
      the laptop sleeps. Wake it. Expected: nothing is inserted, the microphone
      is not in use, and the log shows "Suspending: cancelling any dictation in
      progress".
- [ ] **Recording time limit.** With WinDictate closed, set `"MaxRecordSeconds": 10`
      in `settings.json`, start WinDictate, and hold Ctrl+Alt while speaking for
      about 15 seconds. Expected: recording stops at 10 seconds and that
      dictation is inserted. Still holding the keys does not start another one;
      after releasing, a new hold works. Set the value back to 300 afterwards.

## 7. Trailing space (bug 7)

- [ ] Dictate two sentences in a row into the same field. Expected: "Hello
      there. How are you? " with a space between them.
- [ ] Untick *Add a space after each dictation* and save. Expected: no space
      is added after a dictation.

## 8. Quiet microphone (bug 8)

- [ ] In *Settings → System → Sound → Input*, set the input volume to about
      15-20%. Dictate quietly, with a two-second pause in the middle. Expected:
      it is transcribed, not "Didn't catch that".
- [ ] At that level, hold the hotkey for 3 seconds in silence. Expected: "Didn't
      catch that". Restore the input volume afterwards.

## 9. Microphone after docking (bug 9)

- [ ] In Settings, choose a specific microphone (for example the headset) and
      save. Then dock or undock, or plug in another USB audio device, so that
      Windows re-orders the devices. Dictate. Expected: the chosen microphone
      is still used. Check with the level meter, or speak only into that mic.
- [ ] Unplug the chosen microphone and dictate. Expected: the Windows default
      is used, and the log shows "Chosen microphone is not connected,
      recording from the system default". Settings lists it as "<name> (not
      connected)". Saving other changes keeps it, and it is used again once
      plugged back in.

## 10. The pill appears only when recording (bug 10)

- [ ] With a Bluetooth headset as the microphone (not in a call), hold
      Ctrl+Alt. Expected: "Opening microphone" may show briefly, then the bars.
      Speech that starts right after the bars appear is not clipped.
- [ ] With the same headset, hold the hotkey and let go **before** the bars
      appear (after about a second). Expected: "Microphone wasn't ready", and
      nothing is inserted.

## 11. Listening indicator

The automated Windows tests already check that both styles appear, go through
every state, hide themselves, stay click-through and never take focus. These
need a person and a real screen.

- [ ] **Glass pill.** Dictate into a maximised Word document. Expected: a small
      capsule just above the taskbar, bars moving with your voice; three dots
      while it works; a tick; then it fades. It never covers what you are
      typing and never takes the cursor out of Word.
- [ ] Click where the pill is while it is showing. Expected: the click goes to
      the app underneath, as if the pill were not there.
- [ ] **Edge glow.** In Settings choose *Edge glow*, press **Preview**, then
      Save and dictate. Expected: the top edge of the taskbar lights up and
      breathes with your voice, a highlight travels along it while it works,
      and it brightens once and fades. No shape and no words.
- [ ] In either style, hold the hotkey in silence for 3 seconds. Expected: a
      small pill says "Didn't catch that" for about two seconds.
- [ ] **Second monitor.** With an external monitor attached (ideally at a
      different scaling), dictate into a window on each screen. Expected: the
      indicator appears on the screen of the window you are dictating into,
      centred, the same size on both.
- [ ] Switch Windows between light and dark mode (*Settings → Personalisation →
      Colours*) and dictate. Expected: light glass in light mode, dark glass in
      dark mode.
- [ ] Turn off *Settings → Accessibility → Visual effects → Animation effects*
      and dictate. Expected: the same states, appearing and disappearing
      without motion. Turn it back on afterwards.
- [ ] Share your screen in a Teams test call and dictate. Note whether the
      indicator is visible to the other side (it is drawn like any other
      window).

## 12. Privacy

- [ ] Search the log for a distinctive word you dictated. Expected: not found.
- [ ] Look in `%APPDATA%\WinDictate`. Expected: no audio files, and no
      `transcription-history.tsv` unless you enabled history.
- [ ] With AI cleanup off, watch WinDictate in Resource Monitor (*Network* tab)
      while dictating. Expected: no network activity. The only connection is
      the one-time model download.
