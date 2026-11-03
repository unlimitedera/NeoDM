# Link grabber and custom categories (1.9.0)

## Link grabber

Open it from Tasks > Link grabber, the sidebar button, Ctrl+Shift+V (with the text on the clipboard) or the buttons of the empty download list. You can also drop text, links or a list file (`.txt`, `.csv`, `.m3u`, `.m3u8`, `.json`, `.lst`, `.list`, `.url`, `.html`, `.md`, `.log`, up to 4 MB each) on the main window. A dropped `.torrent` file is added as a torrent instead.

What it does:

1. **Find links.** Any text is searched for `http` and `https` links. Other schemes (ftp, file, magnet, javascript, data, mailto), links with a user name or password, links without a host and links longer than 8192 characters are ignored and counted. Repeats (also when only the `#fragment` differs) are skipped. At most 5000 links are kept and at most 8 million characters of text are read. Trailing punctuation is dropped, and a closing bracket stays only when the link itself holds the opening one.
2. **Stage them.** The table shows a tick box, the file name (guessed from the address, editable), the site, the size and a status. Nothing downloads from here. Links that are already in your downloads start unticked, YouTube links are not offered (use Add URL for the quality choices). The filter box matches name, site, address and extension; the site box limits the table to one host.
3. **Check links (optional).** Asks each site with HEAD, and a one byte ranged GET when HEAD is refused, using the same proxy, identity, referer and compatibility steps as a download. At most four checks run at once, the pass can be stopped, a link that looks single use (a long address with a token-like path part) and stream playlists are never requested. 404 and 410 mean "Not found" and untick the row, a web page answer is flagged "Web page, not a file", every other failure is "Unknown". A name the server gives replaces the guess unless you typed one.
4. **Package and folder.** A package name groups the downloads (shown in the Package column, found by the search box). The files go by the category rules, into a folder you choose, or into a new folder named after the package under the download folder or the chosen folder. The package folder name is made safe like any file name.
5. **Add.** Add to queue starts the downloads in table order within the connection limit of Options > Downloads. Add paused adds them waiting. Duplicates and failures are reported, and the queue is saved once for the whole batch.

What it does not do, by design: it does not read web pages for hidden links, it has no per-site plugins, it does not solve puzzles, skip waiting times or use accounts. A link that returns a web page instead of a file fails with the usual wrong-content message.

### Clipboard watching

Options > Downloads > Link grabber, off by default. When on, copying text with two or more web links opens the grabber. The watcher looks at the clipboard only when Windows reports a change, ignores what was on the clipboard when it was switched on, ignores text NeoDM copied itself, reads at most 1,000,000 characters, offers the same text at most once a minute and any text at most once every four seconds, and sends nothing anywhere.

## Custom categories

Options > Save To > Custom categories. Each category has a name (60 characters at most, unique, not a built-in name), a folder, file extensions and address rules, and can be switched off.

- **Folder:** relative to the download folder, or a full path. Refused: `..`, reserved Windows names (CON, NUL, COM1, LPT1 ...), names ending in a dot or space, device paths (`\\?\`, `\\.\`), drive-relative or rooted paths, a bare drive, and any folder that passes through a link (junction or symbolic link).
- **Extensions:** comma separated, without dots or stars. `tar.gz` matches whole names ending in `.tar.gz`. Presets add the usual lists for documents, archives, audio, video, images, programs and e-books.
- **Address rules:** one per line. `example.com` matches that host and its subdomains; `*` and `?` work as wildcards (`*.example.com` also matches `example.com`); a rule with a slash such as `example.com/files/*` matches host and path of the address. A rule is checked against the address the job was added with (the final address once known) and the host of the referring page. No regular expressions are used.
- **Order:** a folder you pick yourself > your categories from the top of the list, all address rules first and then all extensions > the built-in categories (address rules, then extensions) > the download folder. A category whose folder is unsafe is skipped.
- **Built-in categories** (Programs, Compressed, Music, Video, Documents) keep their folder boxes. Their extensions, address rules and on/off switch can be edited too, but they cannot be deleted, renamed or moved. "Reset built-in" restores their defaults and leaves your categories alone.

The same rules decide the folder of normal downloads, streams, YouTube downloads, torrents, the capture dialog preview and the final saved path (also after the server settles the file name). Settings schema 9 adds `CustomCategories`, `BuiltInCategories` and `WatchClipboardForLinks`; the five folder settings of older versions are kept as they were.
