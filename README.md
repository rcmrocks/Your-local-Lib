# Your Local Lib

A Windows desktop catalog for a personal Libib library. It imports a collection CSV, then checks the shelves with a keyboard-wedge barcode scanner. The scanner types a barcode and presses Enter. There is no scanner SDK and no account.

## Download and install

Version 1.4.1 for 64-bit Windows.

**[Download YourLocalLib-Setup.exe](https://github.com/rcmrocks/Your-local-Lib/releases/latest/download/YourLocalLib-Setup.exe)**

The same file is listed on the [latest release](https://github.com/rcmrocks/Your-local-Lib/releases/latest).

1. Open `YourLocalLib-Setup.exe`.
2. If Windows says it protected your PC, click **More info**, then **Run anyway**. The installer is not signed.
3. Step through the setup. It does not ask for an administrator password. It installs for the current Windows user into `%LOCALAPPDATA%\Programs\Your Local Lib` and adds a Start menu shortcut named Your Local Lib.
4. **Add a shortcut to the desktop** is already checked. Clear it if you do not want a desktop icon.
5. **Open Your Local Lib** is checked on the last page. Leave it checked and press Finish.

The installed program includes the .NET runtime. Nothing else has to be installed. To look around before you import your own library, open **Scan** and press **Try a sample library**.

The window, the Start menu, the installer, and the program folder say **Your Local Lib**. The catalog lives at `%LOCALAPPDATA%\YourLocalLib\library.db`. Opening an older copy moves `%LOCALAPPDATA%\IrishsLibraryTracker` here if the new folder is not there yet. Uninstalling the program leaves the catalog where it is. Opening an older catalog keeps your titles, your tags, and your ratings, and a title you had already rated is marked as done.

Libib catalogs books, movies, music (records, CDs, vinyl), video games, and board games. All five types import, sit on shelves, and filter on Library. Scan a UPC the same way you scan an ISBN. Choose the type on Scan before a new barcode; a barcode already in the catalog keeps the type it has.

## Scanner

The Rabitpos HBA-U27W (and the same style of 2.4 GHz USB-dongle scanner) behaves as a keyboard.

1. Plug in the USB dongle. Pair the scanner the way its manual describes.
2. Use instant-upload mode and a US keyboard layout.
3. Open **Scan**. Pick the type you are holding: Book, Movie, Music, Video game, or Board game. The cursor belongs in the scan box. A barcode is recorded when Enter arrives.
4. Storage mode is different. Click the **Stored scans** box, let the scanner dump its memory, then press **Add stored scans**. Those lines are not delayed, because two real copies can arrive a few milliseconds apart.

A second live scan of the same barcode inside the delay (400 ms unless you change it) is held and is not the undo target. Enter never confirms a move. **Y** or **Move here** accepts. **N** or **Not now** declines. A new barcode while a question is open records Not now, then reads the new barcode. Ctrl+Z undoes the last scan when the scan box is empty or no text box has the cursor. Y, N, Escape, and Ctrl+Z act only on Scan, so typing on another page never answers a question or undoes a scan. Ctrl+F opens Library search from any page. Escape dismisses a question first, then clears the scan box.

Shelf codes look like `LOC-0007`. Print them from **Shelves**. Scanning one switches the shelf and speaks its name.

## First import

On the Libib website, open **Settings** and choose **Export Collection Data**. Export one collection. The file is a CSV and may contain line breaks inside descriptions. Mixed collections and type-only templates both work. A movie template has ensemble and aspect ratio, music has discs, video games have ESRB, board games have players and age group. If the file has no `item_type` column, **This file is** on Import can be left on Auto or set to the type you exported.

In the app, open **Import**, choose that file, read the counts, and import. Importing again updates titles and creators and keeps the shelves already stored. A title you have edited, or a cover you have chosen, keeps those details on the next import. A description found online stays unless the export has its own. A sample library is replaced only after a second confirmation.

A title can arrive with an ISBN-13, an ISBN-10, a UPC, an EAN, or none of those. When an ISBN-10 does not convert to the same ISBN-13, the ISBN-13 is kept.

## Back up and restore

On **Import**, **Save a backup** writes one zip of the catalog, every cover the library points at, your tags, ratings, notes, shelves, sessions, and settings. Plug in a thumb drive first and the save window opens on it. The zip is written beside the name you chose, read back, then moved into place, so a full or unplugged drive never leaves half a file under that name.

**Restore a backup** reads the zip (or a `library-….db` file from the automatic backups folder) without changing anything, then asks before it replaces the library on this computer. A library with titles or shelves of its own is saved first as `before-restore-….zip` in `%LOCALAPPDATA%\YourLocalLib\backups`. A backup from a newer version of the program is refused. A `.db` copy has the catalog but no covers; covers found online come back as you open or scan each title. Cover files already on this computer are kept, and a different image gets its own name.

## How a session behaves

**Place** puts the next unlocated copy on the shelf you are standing at. A copy that already has a shelf stays there.

**Shelf audit** confirms copies on this shelf. A copy recorded somewhere else asks before it is moved, and a copy with no shelf yet asks before it is placed. Tick **Move without asking** and both happen at once. The card says Moved or Placed, and Ctrl+Z puts the copy back. A different edition of a title on your list still asks. Other shelves are not called missing from the library.

**Whole library** asks you to see every copy. Only **Finish audit** writes the list of copies that were not seen. Closing the window does not finish the audit.

## Try it on Windows

1. Install and open Your Local Lib.
2. On Scan, press **Try a sample library**.
3. Leave the type on Book, type `9780000000019`, and press Enter. The card names the sample book.
4. Open Shelves, add a bookcase, add a shelf inside it, and note the `LOC-` code. Hide folds the shelves inside a bookcase back into that row. The shelf list scrolls when it is longer than the window.
5. Back on Scan, type that code and press Enter. The shelf changes.
6. Choose **Shelf audit**. Scan a sample ISBN that already sits on another shelf. Press **Y** to move it, then Ctrl+Z to put it back.
7. Choose **Whole library**, scan one book, and press **Finish audit**. Reports lists the copies that were not seen.
8. Close the app without pressing Finish. The unfinished session is still there the next time it opens.

The sample books are obviously fake and titled `SAMPLE:`. Importing your own CSV removes them after you confirm.

## Your tags and ratings

On Library, the search box, the type and shelf filter, and the rating filter share one row. The number beside **Library** on the left counts the copies the list shows, so it follows the search and the filters. Select a title and tick **Read**, **Watched**, **Listened**, or **Played** when you have finished it. The stars appear only on a finished title. Clearing that mark hides them and keeps the rating for when you tick it again. The stars run from a half star to five. Moving across a star fills it, including the half, and the number beside the stars follows the pointer. Click to keep that rating. **Clear** removes it. The rating list shows one score, or the titles that show no rating. The filter list also offers Done and Not done. A number typed into search is not a rating. Use the list for that.

## Title details

The page for a title is read-only until you press **Edit**. You can change the type, title, creator, series, publisher, date, description, notes, and the extra fields that belong to that type, and you can choose or remove a cover. **Save details** keeps them. **Cancel** drops the unsaved text. A title with no barcode uses the same page, including a description. Add an ISBN or UPC from the edit page when you have the barcode in hand. Search matches the description, the notes, and the publisher. Text on the page can be selected and copied, and **Copy** beside the barcode copies the number. **Delete from library** removes that title after you confirm on the same page. Shelves stay where they are.

Tags are yours as well. Type a word, or several separated by commas, and press Enter. Search matches those words. Opening or scanning a book also adds a few subject tags from the ISBN, such as Fantasy, into those same tags. Removing one keeps it removed. A later Libib import updates the imported title and the imported rating, and leaves your tags and your rating where they are.

## Covers and descriptions from the internet

When a book has an ISBN and lacks a cover, a description, or subject tags, scanning it or opening it on Library looks them up. Open Library answers first, and Google Books fills what is still missing. An album looks up MusicBrainz by barcode and Cover Art Archive for the sleeve. Movies, video games, and board games keep the cover and description from the import; type them in **Edit** if the file did not have them. The card and the title page say Looking up while it runs. Lookups run in the background, at most two at a time, so scanning never waits, and without internet they fail quietly. Once the sites have answered, a title is not looked up again by itself. While something is still missing, **Look up again** on its page asks once more. A description you saved with **Edit** stays as you left it, and a cover you chose or removed stays that way. Sample titles are never looked up.

Google Books often refuses requests that come without a key, and a site that refuses is left alone for ten minutes. A free key from the Google Cloud console allows 1,000 lookups a day. Paste it into **Settings** under **Google Books key** and press **Save**.

## Rebuild the installer

Requires the .NET 8 SDK and NSIS 3.

```bash
dotnet test YourLocalLib.sln -c Release
dotnet publish src/YourLocalLib -c Release -r win-x64 --self-contained true -o publish/win-x64
makensis installer/setup.nsi
```

The setup program is written to `dist/YourLocalLib-Setup.exe`. Attach that file to a GitHub release. It is not stored in git. `publish/` and `dist/` are not part of the git history. The wizard images live in `installer/` and use the same paper, ink, and forest green as the program.

## Support

**Support me on Patreon**, below Settings on the left rail, opens the [Patreon page](https://patreon.com/Rcmrocks?utm_medium=unknown&utm_source=join_link&utm_campaign=creatorshare_creator&utm_content=copyLink) in your web browser.

## What this app does not do

It does not sync with Libib or host an account. A rating, review, or reading status that arrived in the export stays as imported text. The stars on Library are a separate rating that only you set. Nothing fetched online sets a rating. It does not check titles out. A person can be a shelf if you want a "with someone" location. It does not print a unique barcode per copy. The barcode on the item is the barcode.
