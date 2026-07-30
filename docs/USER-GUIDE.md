# PitLaunch Beta 0.9.2 - עברית

PitLaunch היא אפליקציית Windows קלה למחשב שמשמש גם כשולחן עבודה רגיל וגם כסימולטור מרוצים. מגדירים פעם אחת אילו מסכים והתקני שמע שייכים לכל מצב, ולאחר מכן עוברים בין המצבים בלחיצה אחת.

זוהי גרסת בטא. התוכנה עדיין אינה חתומה דיגיטלית, ולכן Windows SmartScreen עשוי להציג אזהרה בהפעלה הראשונה. אין צורך בהרשאות מנהל מערכת; יש להפעיל את PitLaunch באופן רגיל.

## התקנה ועדכונים

יש שתי דרכים להתקין:

- **`PitLaunch-win-Setup.exe` (מומלץ)** — התקנה רגילה עם קיצורי דרך בתפריט התחלה ובשולחן העבודה, והסרה דרך "הוספה או הסרה של תוכניות". רק בדרך זו ניתן לקבל **עדכונים קטנים**: PitLaunch מורידה רק את מה שהשתנה (כ-0.2 MB במקום 72 MB).
- **קובץ ZIP** — יש לחלץ את כל התוכן לתיקייה קבועה ולהפעיל את `PitLaunch.exe`. בגרסה זו כל עדכון מחייב הורדה מחדש של כל התוכנה.

לבדיקת עדכון: **Settings → Updates → Check for updates**, ואז **Install and restart**. הפרופילים נשמרים מחוץ לתיקיית ההתקנה, ולכן עדכון או הסרה אינם מוחקים את המצבים השמורים.

## הגדרה ראשונית

1. חברו למחשב את הציוד של שולחן העבודה ושל הסימולטור, ולאחר מכן פתחו את `PitLaunch.exe`. גם מסך שמושבת כרגע ב-Windows יכול להופיע ב-PitLaunch כל עוד הוא מחובר.
2. לחצו על **Create setup** ובחרו **Desk** או **Sim racing**. עבור סימולטור ניתן לבחור מסך יחיד, שניים, שלושה, ארבעה, Ultrawide או VR.
3. בחרו את המסכים שישמשו במצב, הגדירו מסך ראשי וסידור, ולאחר מכן בחרו יציאת שמע ומיקרופון.
4. לחצו על **Create and switch**. לפני שמירה או שינוי, PitLaunch מבקשת מ-Windows לוודא שתצורת המסכים תקינה.
5. צרו באותה דרך את המצב השני. מיקומי החלונות נשמרים אוטומטית בעת יציאה מכל מצב.

השתמשו בכפתור **Switch** או בתפריט של PitLaunch באזור ההתראות כדי לעבור בין המצבים. במהלך הבטא האפשרות **Confirm before switching** מופעלת כברירת מחדל, ולכן תתבקשו לאשר לפני שינוי מסכים או שמע.

## אמצעי בטיחות בבטא

- הכפתור **Restore displays** בסרגל הצד או בהגדרות מפעיל את כל המסכים ש-Windows מזהה כרגע. הוא אינו משנה את המצבים השמורים.
- קיצור החירום **Ctrl+Alt+Shift+F12** משחזר את המסכים כאשר PitLaunch פועלת, גם כשהחלון מוסתר באזור ההתראות.
- ניתן ללחוץ לחיצה ימנית על סמל PitLaunch באזור ההתראות ולבחור **Restore all displays**.
- ניתן גם להריץ `PitLaunch.exe --restore-displays` מקיצור דרך, Terminal, Stream Deck או עותק נוסף של PitLaunch.
- תצורת השחזור נבדקת לפני ההפעלה. אם Windows דוחה אותה, PitLaunch מנסה להחזיר את תצורת המסכים הקודמת.
- מסך, אוזניות, מיקרופון, אפליקציה או חלון שאינם זמינים יידלגו עם אזהרה במקום לגרום לקריסה.

## פרופילים והגדרות

הפרופילים וההגדרות נשמרים בקובץ:

```text
%APPDATA%\PitLaunch\profiles.json
```

ההגדרות נשמרות גם לאחר סגירת PitLaunch או הפעלה מחדש של Windows. גיבוי של הקובץ נשמר אוטומטית. קובץ האבחון נמצא בנתיב `%APPDATA%\PitLaunch\pitlaunch.log`, וכאשר הוא מגיע ל-2 MB הוא מועבר אל `pitlaunch.log.previous`.

כל פרופיל יכול לכלול תצורת מסכים מלאה, התקני שמע ומיקרופון, מיקומי חלונות, אפליקציות להפעלה או לסגירה, זיהוי תהליכי משחק וקיצור מקשים גלובלי.

## שורת פקודה

```cmd
PitLaunch.exe --profile "Sim Mode"
PitLaunch.exe --capture "Desktop Mode"
PitLaunch.exe --chooser
PitLaunch.exe --background
PitLaunch.exe --restore-displays
PitLaunch.exe --exit
```

רק עותק אחד של PitLaunch פועל. פקודות מעותקים נוספים מועברות לעותק שפועל באזור ההתראות.

## הפעלה עם Windows

פתחו את **Settings**, הפעילו **Start with Windows**, והשאירו את **Show setup chooser after sign-in** פעיל כדי לבחור Desk או Sim racing לאחר הכניסה ל-Windows. כיבוי אפשרות הבחירה יפעיל את PitLaunch בשקט באזור ההתראות.

ההפעלה הרגילה נרשמת גם במפתח ההפעלה של המשתמש הנוכחי וגם בתיקיית Startup, ואינה דורשת הרשאות מנהל מערכת. אם Windows עדיין מדלג עליה במחשב מסוים, ניתן ללחוץ על כפתור המגן **Reliable startup**. אפשרות זו מבקשת אישור מנהל פעם אחת כדי להתקין משימת כניסה מושהית כגיבוי. PitLaunch עצמה, וכל אפליקציה שהיא מפעילה, ממשיכות לפעול בהרשאות רגילות. ניתן להסיר את הגיבוי מאותו כפתור.

## שליחת דיווח על תקלה

צרפו:

1. מה לחצתם ומה ציפיתם שיקרה.
2. מה קרה בפועל, כולל אזהרה שהוצגה.
3. את הקובץ `%APPDATA%\PitLaunch\pitlaunch.log` מהמחשב שבו קרתה התקלה.
4. האם מסך, אוזניות, מיקרופון או תחנת עגינה היו מנותקים.

כפתור הסגירה מעביר את PitLaunch לאזור ההתראות. לפני החלפה או מחיקה של קובץ התוכנה, לחצו לחיצה ימנית על הסמל ובחרו **Exit**.

---

# PitLaunch Beta 0.9.2 - English

PitLaunch is a lightweight Windows profile switcher for a PC shared between a desk and a sim rig. Choose the screens and sound devices for each setup once, then restore the whole PC with one click.

This is a beta build. PitLaunch is unsigned, so Windows SmartScreen may show a warning on first launch. PitLaunch does not need administrator access; run it normally.

## Installing and updating

Two ways to install:

- **`PitLaunch-win-Setup.exe` (recommended)** — a normal install with Start Menu and Desktop shortcuts, removable from Add or Remove Programs. Only this one gets **small updates**: PitLaunch downloads just the parts that changed, around 0.2 MB instead of 72 MB.
- **The ZIP** — extract everything to a permanent folder and run `PitLaunch.exe`. Updating this copy means downloading the whole app again.

To update, open **Settings → Updates → Check for updates**, then **Install and restart**. Profiles are stored outside the install folder, so updating or uninstalling never removes your saved setups.

## First setup

1. Connect the desk and rig devices to the PC, then open `PitLaunch.exe`. A monitor may be disabled in Windows; PitLaunch still discovers its preferred resolution and refresh rate while it is connected.
2. Click **Create setup** and choose **Desk** or **Sim racing**. Sim setups can be Single screen, Dual screen, Triple screen, Quad screen, Ultrawide, or VR.
3. Choose the monitor tiles to use, select the main screen and arrangement, then pick the sound output and microphone. PitLaunch recommends the screens already active for Desk and screens outside the Desk profile for Sim racing.
4. Click **Create and switch**. PitLaunch asks Windows to validate the exact display plan before saving or changing anything.
5. Create the other setup the same way. Window positions begin learning automatically as you leave each setup, so there is no separate window-arranging step during setup.

Use **Switch** or the PitLaunch tray menu to change setups. During beta, **Confirm before switching** defaults to on. PitLaunch saves the windows from the setup you are leaving, restores the selected display and audio state, starts its configured apps, restores window positions, and then stays quietly in the tray.

## Beta safety controls

- **Restore displays** in the sidebar or Settings enables every monitor Windows currently detects. It does not overwrite saved setups.
- Press **Ctrl+Alt+Shift+F12** for emergency display recovery when PitLaunch is running, even if its window is hidden in the tray.
- Right-click the tray icon and choose **Restore all displays** for the same recovery action.
- Run `PitLaunch.exe --restore-displays` from a shortcut, terminal, Stream Deck, or another copy of PitLaunch.
- Display recovery is validated before applying. If Windows rejects it, PitLaunch attempts to restore the previous topology.
- A disconnected monitor, headset, microphone, app, or window is skipped with a warning instead of crashing the switch.

## Profiles and settings

Profiles are stored in:

```text
%APPDATA%\PitLaunch\profiles.json
```

The file is normal JSON and can be edited while PitLaunch is closed. It supports unlimited profiles. Settings are saved in the same file and survive app restarts and Windows restarts. The app also keeps a backup copy and writes diagnostics to `%APPDATA%\PitLaunch\pitlaunch.log`. Logs rotate at 2 MB to `pitlaunch.log.previous`.

Each profile can include:

- Full Windows display topology built and validated through the CCD API, including enabled screens, primary display, positions, resolution, refresh rate, rotation, and scaling.
- Default playback, communications, and microphone devices. These are editable on the profile page: pick a different device from the dropdowns and save, without recapturing the whole profile.
- Positions for normal top-level application windows.
- Applications to start on activation and optionally close on deactivation.
- Process names that automatically activate the profile while a game is running. The process field lists the apps currently running on the PC, and typing a name manually still works.
- A global hotkey, such as `Ctrl+Alt+F9`. Click the hotkey box and press the actual key combination to record it; Backspace clears it.
- A setup identity and display variant so the startup chooser clearly distinguishes a desk from a single-screen, triple-screen, ultrawide, or VR sim rig.

If a saved monitor, audio device, application, or window is unavailable, PitLaunch skips it, reports a warning, and continues with the parts it can restore. Display plans are validated before they are saved, display changes are validated again before switching, and the previous topology is rolled back if Windows rejects an apply. If Windows rejects exact saved refresh rates, PitLaunch retries the same layout with default refresh rates instead of failing.

## Command line

Activate a profile from Stream Deck, a wheel button, or a shortcut:

```cmd
PitLaunch.exe --profile "Sim Mode"
```

Other commands:

```cmd
PitLaunch.exe --capture "Desktop Mode"
PitLaunch.exe --chooser
PitLaunch.exe --background
PitLaunch.exe --restore-displays
PitLaunch.exe --exit
```

Only one copy runs. Commands sent by later copies are forwarded to the tray instance.

## Windows startup

Open **Settings** in PitLaunch, enable **Start with Windows**, and leave **Show setup chooser after sign-in** on. At the next Windows sign-in, choose **Use setup** on the Desk or Sim racing card. PitLaunch applies the selected displays, sound, windows, and apps, then returns to the tray.

Turn the chooser setting off if PitLaunch should start silently in the tray instead. Background startup creates no visible window. Normal startup uses both a current-user `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` entry and a shortcut in the current user's Startup folder. It does not require administrator rights.

If Windows still skips normal startup on a particular PC, use the shield-marked **Reliable startup** button. It requests administrator approval once to install a delayed sign-in task as a fallback. The task is explicitly registered at limited privilege: PitLaunch itself and every app it launches still run normally, not as administrator. The same button removes the fallback.

PitLaunch targets 64-bit Windows 10/11 and is published as a self-contained .NET 8 executable. No separate .NET installation is required for the packaged app.

## Sending a beta bug report

Include:

1. What you clicked and what you expected.
2. What actually happened, including any warning shown by PitLaunch.
3. `%APPDATA%\PitLaunch\pitlaunch.log` from the affected PC.
4. Whether any monitor, headset, microphone, or dock was disconnected at the time.

The close button sends PitLaunch to the tray. Use **Exit** from the tray menu before replacing or deleting the EXE.
